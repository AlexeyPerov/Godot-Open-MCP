#if TOOLS
#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Godot;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Reflection tool family (P5.1) — first-party C# member discovery + method invoke. Two tools:
    /// <list type="bullet">
    /// <item><description><c>godot_open_mcp_reflection_method_find</c> — bounded, structured member
    /// discovery across loaded assemblies (read-only, gate-free, group <c>reflection</c>).</description>
    /// </item>
    /// <item><description><c>godot_open_mcp_reflection_method_call</c> — invoke a resolved static or
    /// instance method (mutating, default gate <c>enforce</c>, group <c>reflection</c>). Godot API–
    /// touching instance calls run on the main thread.</description></item>
    /// </list>
    ///
    /// <para>
    /// <b>Adapted from Unity Open MCP's MetaTools</b> (adapt fidelity):
    /// <list type="bullet">
    /// <item><description><c>FindMembersTool.cs</c> → the assembly/type walk, the
    /// <c>ShouldIncludeAssembly</c> filter, the bounded-result + <c>truncated</c> contract, and the
    /// structured member serializers (type/method/property with returnType/parameters[]/isStatic/
    /// isGeneric/genericParameters[]). The Unity <c>include_unity_editor</c> toggle becomes
    /// <c>include_godot_editor</c> (filter <c>GodotSharpEditor</c> / <c>*Editor</c> assemblies).
    /// </description></item>
    /// <item><description><c>InvokeMethodTool.cs</c> → the <c>FindType</c> full-name-then-simple-name
    /// resolver, the overload disambiguation (<c>ResolveOverload</c> + <c>arg_type_names</c>), generic
    /// binding (<c>BindGenericMethod</c> + <c>generic_arg_types</c>), CLR alias table, and the
    /// <c>ConvertArgs</c>/<c>ConvertArg</c> coercion for primitives/enums. The Unity <c>object_id</c>-
    /// first instance targeting is replaced by Godot-native <c>node_path</c> as the primary target;
    /// <c>Activator.CreateInstance</c> is restricted to pure POCOs (never GodotObject subclasses).
    /// </description></item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>Godot-MCP reference (behavior only)</b>: the <c>executeInMainThread</c> opt-out and the
    /// "thread-safe pure logic vs Godot-API-touching" distinction are lifted from
    /// <c>Tool_Reflection.MethodCall</c>. The disallowed ReflectorNet engine (<c>MethodRef</c>,
    /// <c>SerializedMember</c>, <c>MethodData</c>, <c>MethodWrapper</c>) is explicitly NOT ported
    /// (ADR-004 / P5.1 fidelity table).
    /// </para>
    ///
    /// Editor-only (<c>#if TOOLS</c>): the handlers touch <see cref="EditorInterface"/> (for
    /// <c>node_path</c> resolution), <see cref="AppDomain.CurrentDomain.GetAssemblies"/>, and live
    /// Godot <see cref="GodotObject"/> instances. The pure-managed body parsers
    /// (<see cref="ReflectionMethodFindBody"/>, <see cref="ReflectionMethodCallBody"/>) live outside
    /// this guard and are unit-tested.
    /// </summary>
    internal static class ReflectionTools
    {
        /// <summary>The MCP tool name for the member finder (P5.1).</summary>
        internal const string ReflectionMethodFindToolName = "godot_open_mcp_reflection_method_find";

        /// <summary>The MCP tool name for the method invoker (P5.1).</summary>
        internal const string ReflectionMethodCallToolName = "godot_open_mcp_reflection_method_call";

        // --- registration -----------------------------------------------------------

        /// <summary>
        /// Register the reflection tool family. P5.1 adds one read-only discovery tool
        /// (<c>godot_open_mcp_reflection_method_find</c>, group <c>reflection</c>, default gate
        /// <c>off</c> — read-only) and one gated invoker (<c>godot_open_mcp_reflection_method_call</c>,
        /// group <c>reflection</c>, default gate <c>enforce</c> — invokes can mutate project/editor
        /// state). Registered once at plugin enable; safe to call again on re-enable (the registry is
        /// idempotent).
        /// </summary>
        internal static void RegisterReflectionTools()
        {
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: ReflectionMethodFindToolName,
                isMutating: false,
                defaultGate: "off",
                group: "reflection",
                handler: Find));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: ReflectionMethodCallToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "reflection",
                handler: Call));
        }

        // --- godot_open_mcp_reflection_method_find ----------------------------------

        /// <summary>
        /// Handler for <c>godot_open_mcp_reflection_method_find</c>. Read-only. Walks the loaded
        /// assemblies (filtered by <c>assembly_filter</c> / <c>include_godot_editor</c> /
        /// <c>include_project</c>) and returns bounded, structured member entries matching
        /// <c>query</c> against type/member names. When <c>type_name</c> is set, the walk drills into
        /// that single declaring type.
        ///
        /// <para>
        /// Result shape: <c>{ "members": [...], "returned": N, "truncated": M, "query": "..." }</c>.
        /// Each member carries <c>kind</c> (type/method/property), <c>name</c>, <c>declaringType</c>,
        /// <c>assembly</c>, a flat <c>signature</c> (when <c>include_signatures</c>), and structured
        /// fields (returnType/parameters[]/isStatic/isGeneric/genericParameters[] for methods;
        /// propertyType/canRead/canWrite for properties). Overloads are listed separately so an agent
        /// can pick one for <c>reflection_method_call</c>.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult Find(string body)
        {
            var request = ReflectionMethodFindBody.Parse(body);

            var results = new List<string>();
            int totalMatches = 0;
            bool capReached = false;

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();

            // Single-type drill-down: resolve the type once and enumerate its members directly.
            if (!string.IsNullOrEmpty(request.TypeName))
            {
                var type = FindType(request.TypeName, request.AssemblyFilter);
                if (type == null)
                    return ToolDispatchResult.Fail("type_not_found",
                        $"Type '{request.TypeName}' not found in any loaded assembly. " +
                        "Use the fully qualified name including namespace, or run a broad query to discover it.");

                AppendTypeMembers(type, request, results, ref totalMatches, ref capReached);
            }
            else
            {
                foreach (var asm in assemblies)
                {
                    if (!ShouldIncludeAssembly(asm, request)) continue;

                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch { continue; }

                    foreach (var type in types)
                    {
                        try
                        {
                            AppendTypeMembers(type, request, results, ref totalMatches, ref capReached);
                        }
                        catch { }
                    }
                }
            }

            int truncated = capReached ? totalMatches - results.Count : 0;
            var sb = new StringBuilder(results.Count * 128 + 64);
            sb.Append("{\"members\":[");
            for (int i = 0; i < results.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(results[i]);
            }
            sb.Append("],\"returned\":").Append(results.Count);
            sb.Append(",\"truncated\":").Append(truncated);
            sb.Append(",\"query\":");
            sb.Append(BridgeJson.EscapeString(request.Query));
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        static void AppendTypeMembers(Type type, ReflectionMethodFindBody request,
            List<string> results, ref int totalMatches, ref bool capReached)
        {
            var query = request.Query;
            if (request.WantsTypes)
            {
                if (MatchesQuery(type.Name, query) || MatchesQuery(type.FullName, query))
                {
                    totalMatches++;
                    if (results.Count < request.MaxResults)
                        results.Add(SerializeType(type, request.IncludeSignatures));
                    else
                        capReached = true;
                }
            }
            if (request.WantsMethods)
            {
                foreach (var method in GetMethodsSafe(type))
                {
                    if (MatchesQuery(method.Name, query))
                    {
                        totalMatches++;
                        if (results.Count < request.MaxResults)
                            results.Add(SerializeMethod(type, method, request.IncludeSignatures));
                        else
                            capReached = true;
                    }
                }
            }
            if (request.WantsProperties)
            {
                foreach (var prop in GetPropertiesSafe(type))
                {
                    if (MatchesQuery(prop.Name, query))
                    {
                        totalMatches++;
                        if (results.Count < request.MaxResults)
                            results.Add(SerializeProperty(type, prop, request.IncludeSignatures));
                        else
                            capReached = true;
                    }
                }
            }
        }

        static bool ShouldIncludeAssembly(Assembly asm, ReflectionMethodFindBody request)
        {
            var name = asm.GetName().Name ?? string.Empty;

            // An explicit assembly simple-name contains filter wins over the include flags.
            if (!string.IsNullOrEmpty(request.AssemblyFilter))
                return name.IndexOf(request.AssemblyFilter, StringComparison.OrdinalIgnoreCase) >= 0;

            if (!request.IncludeGodotEditor)
            {
                if (name.StartsWith("GodotSharpEditor", StringComparison.OrdinalIgnoreCase) ||
                    (name.StartsWith("Godot.", StringComparison.OrdinalIgnoreCase) && name.Contains("Editor")))
                    return false;
            }
            // When include_project is false, exclude the game/scripts assembly only (it is the one
            // assembly not prefixed by a framework/engine root). Framework + engine assemblies stay.
            if (!request.IncludeProject)
            {
                var isFrameworkOrEngine =
                    name.StartsWith("Godot", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("System", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("mscorlib", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase);
                if (!isFrameworkOrEngine)
                    return false;
            }
            return true;
        }

        static bool MatchesQuery(string? name, string query)
        {
            if (string.IsNullOrEmpty(query)) return true;
            if (string.IsNullOrEmpty(name)) return false;
            return name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static IEnumerable<MethodInfo> GetMethodsSafe(Type type)
        {
            try
            {
                return type.GetMethods(BindingFlags.Public | BindingFlags.Instance |
                    BindingFlags.Static | BindingFlags.DeclaredOnly);
            }
            catch { return Array.Empty<MethodInfo>(); }
        }

        static IEnumerable<PropertyInfo> GetPropertiesSafe(Type type)
        {
            try
            {
                return type.GetProperties(BindingFlags.Public | BindingFlags.Instance |
                    BindingFlags.Static | BindingFlags.DeclaredOnly);
            }
            catch { return Array.Empty<PropertyInfo>(); }
        }

        static string TypeDisplayName(Type? type)
        {
            if (type == null) return "null";
            var name = type.Name;
            if (type.IsGenericType)
            {
                var tick = name.IndexOf('`');
                if (tick > 0) name = name.Substring(0, tick);
                var args = type.GetGenericArguments();
                name += "<" + string.Join(", ", args.Select(TypeDisplayName)) + ">";
            }
            return name;
        }

        static string GetMethodSignature(MethodInfo method)
        {
            try
            {
                var sb = new StringBuilder(64);
                sb.Append(TypeDisplayName(method.ReturnType)).Append(' ').Append(method.Name);
                if (method.IsGenericMethod)
                {
                    sb.Append('<');
                    sb.Append(string.Join(", ", method.GetGenericArguments().Select(TypeDisplayName)));
                    sb.Append('>');
                }
                sb.Append('(');
                var parms = method.GetParameters();
                for (int i = 0; i < parms.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(TypeDisplayName(parms[i].ParameterType)).Append(' ').Append(parms[i].Name);
                }
                sb.Append(')');
                return sb.ToString();
            }
            catch { return string.Empty; }
        }

        static string GetPropertySignature(PropertyInfo prop)
        {
            try
            {
                var sb = new StringBuilder(32);
                sb.Append(TypeDisplayName(prop.PropertyType)).Append(' ').Append(prop.Name).Append(" { ");
                if (prop.CanRead) sb.Append("get; ");
                if (prop.CanWrite) sb.Append("set; ");
                sb.Append('}');
                return sb.ToString();
            }
            catch { return string.Empty; }
        }

        static string GetSummary(Type type) =>
            type.IsClass ? "class" : type.IsInterface ? "interface" : type.IsEnum ? "enum" :
            type.IsValueType ? "struct" : string.Empty;

        static string SerializeType(Type type, bool includeSignatures)
        {
            var sb = new StringBuilder(160);
            sb.Append("{\"kind\":\"type");
            sb.Append("\",\"name\":").Append(BridgeJson.EscapeString(type.Name));
            sb.Append(",\"fullName\":").Append(BridgeJson.EscapeString(type.FullName ?? type.Name));
            sb.Append(",\"namespace\":").Append(BridgeJson.EscapeString(type.Namespace ?? string.Empty));
            sb.Append(",\"assembly\":").Append(BridgeJson.EscapeString(type.Assembly.GetName().Name ?? string.Empty));
            sb.Append(",\"isEnum\":").Append(type.IsEnum ? "true" : "false");
            sb.Append(",\"isClass\":").Append(type.IsClass ? "true" : "false");
            sb.Append(",\"summary\":").Append(BridgeJson.EscapeString(GetSummary(type)));
            if (includeSignatures)
            {
                sb.Append(",\"signature\":").Append(BridgeJson.EscapeString(type.FullName ?? type.Name));
            }
            sb.Append('}');
            return sb.ToString();
        }

        static string SerializeMethod(Type declaringType, MethodInfo method, bool includeSignatures)
        {
            var sb = new StringBuilder(256);
            sb.Append("{\"kind\":\"method");
            sb.Append("\",\"name\":").Append(BridgeJson.EscapeString(method.Name));
            sb.Append(",\"declaringType\":").Append(BridgeJson.EscapeString(declaringType.FullName ?? declaringType.Name));
            sb.Append(",\"assembly\":").Append(BridgeJson.EscapeString(declaringType.Assembly.GetName().Name ?? string.Empty));
            sb.Append(",\"isStatic\":").Append(method.IsStatic ? "true" : "false");
            sb.Append(",\"isGeneric\":").Append(method.IsGenericMethod ? "true" : "false");
            sb.Append(",\"returnType\":").Append(BridgeJson.EscapeString(TypeDisplayName(method.ReturnType)));

            var genericArgs = method.GetGenericArguments();
            sb.Append(",\"genericParameters\":[");
            for (int i = 0; i < genericArgs.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\":").Append(BridgeJson.EscapeString(genericArgs[i].Name));
                sb.Append(",\"constraints\":[");
                Type[]? constraints = null;
                try { constraints = genericArgs[i].GetGenericParameterConstraints(); }
                catch { constraints = null; }
                if (constraints != null)
                {
                    for (int c = 0; c < constraints.Length; c++)
                    {
                        if (c > 0) sb.Append(',');
                        sb.Append(BridgeJson.EscapeString(TypeDisplayName(constraints[c])));
                    }
                }
                sb.Append("]}");
            }
            sb.Append(']');

            var parms = method.GetParameters();
            sb.Append(",\"parameters\":[");
            for (int i = 0; i < parms.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\":").Append(BridgeJson.EscapeString(parms[i].Name ?? string.Empty));
                sb.Append(",\"type\":").Append(BridgeJson.EscapeString(TypeDisplayName(parms[i].ParameterType)));
                bool hasDefault;
                try { hasDefault = parms[i].HasDefaultValue; }
                catch { hasDefault = false; }
                sb.Append(",\"hasDefault\":").Append(hasDefault ? "true" : "false");
                sb.Append('}');
            }
            sb.Append(']');
            if (includeSignatures)
            {
                sb.Append(",\"signature\":").Append(BridgeJson.EscapeString(GetMethodSignature(method)));
            }
            sb.Append('}');
            return sb.ToString();
        }

        static string SerializeProperty(Type declaringType, PropertyInfo prop, bool includeSignatures)
        {
            var sb = new StringBuilder(192);
            sb.Append("{\"kind\":\"property");
            sb.Append("\",\"name\":").Append(BridgeJson.EscapeString(prop.Name));
            sb.Append(",\"declaringType\":").Append(BridgeJson.EscapeString(declaringType.FullName ?? declaringType.Name));
            sb.Append(",\"assembly\":").Append(BridgeJson.EscapeString(declaringType.Assembly.GetName().Name ?? string.Empty));
            sb.Append(",\"propertyType\":").Append(BridgeJson.EscapeString(TypeDisplayName(prop.PropertyType)));
            sb.Append(",\"canRead\":").Append(prop.CanRead ? "true" : "false");
            sb.Append(",\"canWrite\":").Append(prop.CanWrite ? "true" : "false");
            MethodInfo? getter = null;
            try { getter = prop.GetMethod; } catch { }
            sb.Append(",\"isStatic\":").Append(getter != null && getter.IsStatic ? "true" : "false");
            if (includeSignatures)
            {
                sb.Append(",\"signature\":").Append(BridgeJson.EscapeString(GetPropertySignature(prop)));
            }
            sb.Append('}');
            return sb.ToString();
        }

        // --- godot_open_mcp_reflection_method_call ----------------------------------

        /// <summary>
        /// Handler for <c>godot_open_mcp_reflection_method_call</c>. Mutating (gated). Resolves a
        /// method by type + name (with overload/generic disambiguation), resolves the target (static →
        /// null; instance → <c>node_path</c> from the edited scene, or <c>object_id</c> when a stable
        /// handle registry exists, or <c>Activator</c> for pure POCOs), coerces args, invokes, and
        /// serializes the return value with depth/cycle guards.
        ///
        /// <para>
        /// <b>Targeting rules (intentional Godot design — see P5.1 §Targeting rules):</b>
        /// <list type="bullet">
        /// <item><description>Static (<c>is_static:true</c>) — no instance.</description></item>
        /// <item><description><c>node_path</c> — resolve via the edited scene root (reuses
        /// <see cref="NodeTools.ResolvePath"/>). Primary instance target in v1.</description></item>
        /// <item><description><c>object_id</c> — only honored against a stable handle registry. v1 has
        /// no such registry, so a non-zero <c>object_id</c> surfaces <c>unsupported_target</c> rather
        /// than silently fabricating an instance.</description></item>
        /// <item><description>Neither (instance method, no target) — fail <c>missing_target</c>. We do
        /// NOT <c>Activator.CreateInstance</c> Godot engine types (<see cref="GodotObject"/>
        /// subclasses); <c>Activator</c> is allowed only for pure POCOs.</description></item>
        /// </list>
        /// </para>
        ///
        /// <para>
        /// <b>Main thread:</b> the dispatcher already marshals every tool handler to the main thread
        /// (see <c>BridgeHttpServer.DispatchOnMainThread</c>), so the invoke runs there unconditionally
        /// in v1. The <c>execute_in_main_thread</c> flag is accepted and echoed but is a forward-compat
        /// no-op until an off-thread invoke path is introduced (documented in the result).
        /// </para>
        /// </summary>
        internal static ToolDispatchResult Call(string body)
        {
            var request = ReflectionMethodCallBody.Parse(body);

            if (string.IsNullOrEmpty(request.TypeName))
                return ToolDispatchResult.Fail("validation_error",
                    "Field 'type_name' is required and must be non-empty.");
            if (string.IsNullOrEmpty(request.MethodName))
                return ToolDispatchResult.Fail("validation_error",
                    "Field 'method_name' is required and must be non-empty.");

            var type = FindType(request.TypeName, request.AssemblyName);
            if (type == null)
                return ToolDispatchResult.Fail("type_not_found",
                    $"Type '{request.TypeName}' not found" +
                    (request.AssemblyName != null ? $" in assembly '{request.AssemblyName}'" : " in any loaded assembly") +
                    ". Use the fully qualified name including namespace, or run " +
                    "'godot_open_mcp_reflection_method_find' to discover available types.");

            // Resolve the method (overload + generic binding).
            MethodInfo? method;
            if (request.ArgTypeNames != null && request.ArgTypeNames.Length > 0)
            {
                method = ResolveOverload(type, request.MethodName, request.ArgTypeNames, request.GenericArgTypes);
                if (method == null)
                    return ToolDispatchResult.Fail("method_not_found",
                        $"No overload of '{request.MethodName}' on '{type.FullName}' matches arg_type_names " +
                        $"[{string.Join(", ", request.ArgTypeNames)}]. Use reflection_method_find with " +
                        "type_name + kind:method to list overloads.");
            }
            else
            {
                var bindingFlags = BindingFlags.Public | BindingFlags.FlattenHierarchy;
                bindingFlags |= request.IsStatic ? BindingFlags.Static : BindingFlags.Instance;
                try { method = type.GetMethod(request.MethodName, bindingFlags); }
                catch (AmbiguousMatchException)
                {
                    // Multiple overloads share the name and no arg_type_names were supplied.
                    var overloads = SafeGetMethodsByName(type, request.MethodName, bindingFlags);
                    return ToolDispatchResult.Fail("ambiguous_match",
                        $"'{request.MethodName}' on '{type.FullName}' has {overloads.Count} overload(s). " +
                        "Supply arg_type_names to pick one. Overloads:\n" +
                        string.Join("\n", overloads.Take(10).Select(m => "  " + GetMethodSignature(m))) +
                        (overloads.Count > 10 ? $"\n  ...and {overloads.Count - 10} more." : string.Empty));
                }
                if (method == null)
                {
                    var available = SafeGetMethodNames(type, bindingFlags);
                    return ToolDispatchResult.Fail("method_not_found",
                        $"Method '{request.MethodName}' not found on type '{type.FullName}'. " +
                        "Available methods: " + string.Join(", ", available.Take(10)) +
                        (available.Count > 10 ? "..." : string.Empty));
                }

                // Generic method with explicit type args: bind them now.
                if (request.GenericArgTypes != null && request.GenericArgTypes.Length > 0)
                {
                    if (!method.IsGenericMethod)
                        return ToolDispatchResult.Fail("generic_arg_mismatch",
                            $"Method '{request.MethodName}' is not generic, but generic_arg_types were supplied.");
                    if (method.GetGenericArguments().Length != request.GenericArgTypes.Length)
                        return ToolDispatchResult.Fail("generic_arg_mismatch",
                            $"Method '{request.MethodName}' has {method.GetGenericArguments().Length} " +
                            $"generic parameter(s) but {request.GenericArgTypes.Length} generic_arg_types were supplied.");
                    method = BindGenericMethod(method, request.GenericArgTypes);
                    if (method == null)
                        return ToolDispatchResult.Fail("generic_arg_not_found",
                            "One or more generic_arg_types could not be resolved. " +
                            "Use fully qualified type names.");
                }
            }

            // Coerce args.
            ParameterInfo[] parameters;
            try { parameters = method.GetParameters(); }
            catch { parameters = Array.Empty<ParameterInfo>(); }

            object[] invokeArgs;
            try
            {
                invokeArgs = ConvertArgs(request.Args, parameters);
            }
            catch (ReflectionArgException argEx)
            {
                return ToolDispatchResult.Fail("invalid_argument", argEx.Message);
            }
            if (invokeArgs.Length != parameters.Length)
                return ToolDispatchResult.Fail("invalid_argument",
                    $"Argument count mismatch: supplied {invokeArgs.Length}, " +
                    $"'{request.MethodName}' expects {parameters.Length}.");

            // Resolve the instance target.
            object? target = null;
            if (!request.IsStatic)
            {
                if (!string.IsNullOrEmpty(request.NodePath))
                {
                    var root = EditorInterface.Singleton.GetEditedSceneRoot();
                    if (root == null)
                        return ToolDispatchResult.Fail("no_edited_scene",
                            "No scene is currently being edited; cannot resolve node_path instance target.");
                    var node = NodeTools.ResolvePath(root, request.NodePath!);
                    if (node == null)
                        return ToolDispatchResult.Fail("missing_target",
                            $"Node '{request.NodePath}' not found in the edited scene.");
                    if (!type.IsInstanceOfType(node))
                        return ToolDispatchResult.Fail("type_mismatch",
                            $"Resolved node (type '{node.GetType().FullName}') is not assignable to '{type.FullName}'.");
                    target = node;
                }
                else if (request.ObjectId != 0)
                {
                    // No stable handle registry in v1 — surface a discoverable error rather than
                    // silently fabricating an instance or mis-resolving a stale id.
                    return ToolDispatchResult.Fail("unsupported_target",
                        $"object_id {request.ObjectId} cannot be resolved — no stable handle registry is " +
                        "available in v1. Use node_path to target a scene node, or is_static:true for a static method.");
                }
                else
                {
                    // No target supplied. Allow Activator only for pure POCOs — never for GodotObject
                    // subclasses (constructing an engine type off-thread / outside its lifecycle is unsafe).
                    if (typeof(GodotObject).IsAssignableFrom(type))
                        return ToolDispatchResult.Fail("missing_target",
                            $"Instance method '{request.MethodName}' on Godot engine type '{type.FullName}' " +
                            "requires a node_path target (Activator is not used for GodotObject subclasses).");
                    try { target = Activator.CreateInstance(type); }
                    catch (Exception e)
                    {
                        return ToolDispatchResult.Fail("instantiation_error",
                            $"Cannot create instance of '{type.FullName}': {e.Message}. " +
                            "Use is_static:true for static methods, or pass node_path to target a scene node.");
                    }
                }
            }

            // Invoke. The dispatcher already runs this handler on the main thread; execute_in_main_thread
            // is honored as a forward-compat flag (an off-thread path is deferred).
            object? result;
            try
            {
                result = method.Invoke(target, invokeArgs);
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException;
                return ToolDispatchResult.Fail("invoke_failed",
                    $"{(inner?.GetType().FullName ?? "Exception")}: {inner?.Message ?? tie.Message}");
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("invoke_failed", $"{e.GetType().FullName}: {e.Message}");
            }

            // Serialize the return value.
            var sb = new StringBuilder(256);
            sb.Append("{\"returned\":");
            if (method.ReturnType == typeof(void))
            {
                sb.Append("null");
            }
            else
            {
                AppendValue(sb, result, request.MaxDepth, request.MaxItems, 0,
                    new HashSet<object>(ReferenceComparer.Instance));
            }
            sb.Append(",\"void\":").Append(method.ReturnType == typeof(void) ? "true" : "false");
            sb.Append(",\"type_name\":").Append(BridgeJson.EscapeString(type.FullName ?? type.Name));
            sb.Append(",\"method_name\":").Append(BridgeJson.EscapeString(method.Name));
            sb.Append(",\"execute_in_main_thread\":").Append(request.ExecuteInMainThread ? "true" : "false");
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // --- type + method resolution -----------------------------------------------

        /// <summary>
        /// Resolve a type by full name (preferred) then simple name across loaded assemblies, with an
        /// optional assembly simple-name disambiguator. Adapted (copy) from Unity's
        /// <c>InvokeMethodTool.FindType</c>.
        /// </summary>
        static Type? FindType(string typeName, string? assemblyName)
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();

            if (!string.IsNullOrEmpty(assemblyName))
            {
                var asm = assemblies.FirstOrDefault(a => (a.GetName().Name ?? string.Empty) == assemblyName);
                return asm?.GetType(typeName);
            }

            foreach (var asm in assemblies)
            {
                var t = asm.GetType(typeName);
                if (t != null) return t;
            }

            // Simple-name fallback (case-sensitive).
            foreach (var asm in assemblies)
            {
                try
                {
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.Name == typeName) return t;
                    }
                }
                catch { }
            }
            return null;
        }

        static MethodInfo? ResolveOverload(Type type, string methodName, string[] argTypeNames, string[]? genericArgTypeNames)
        {
            MethodInfo[] candidates;
            try
            {
                candidates = type.GetMethods(BindingFlags.Public | BindingFlags.Instance |
                    BindingFlags.Static | BindingFlags.FlattenHierarchy);
            }
            catch { return null; }

            MethodInfo? fallback = null;
            foreach (var m in candidates)
            {
                if (m.Name != methodName) continue;
                var parms = m.GetParameters();
                if (parms.Length != argTypeNames.Length) continue;

                bool match = true;
                for (int i = 0; i < parms.Length; i++)
                {
                    if (!TypeNameMatches(parms[i].ParameterType, argTypeNames[i])) { match = false; break; }
                }
                if (!match) continue;

                if (genericArgTypeNames == null || genericArgTypeNames.Length == 0)
                {
                    if (!m.IsGenericMethod) return m;
                    fallback ??= m;
                }
                else
                {
                    if (!m.IsGenericMethod) continue;
                    if (m.GetGenericArguments().Length != genericArgTypeNames.Length) continue;
                    var bound = BindGenericMethod(m, genericArgTypeNames);
                    if (bound != null) return bound;
                }
            }
            return fallback;
        }

        static bool TypeNameMatches(Type paramType, string requestedName)
        {
            if (string.IsNullOrEmpty(requestedName)) return false;
            if (!string.IsNullOrEmpty(paramType.FullName) && paramType.FullName == requestedName) return true;
            if (paramType.Name == requestedName) return true;
            if (ClrAliases.TryGetValue(requestedName, out var aliasName) &&
                (paramType.Name == aliasName || paramType.FullName == aliasName)) return true;
            return false;
        }

        static MethodInfo? BindGenericMethod(MethodInfo method, string[] genericArgTypeNames)
        {
            var typeArgs = new Type[genericArgTypeNames.Length];
            for (int i = 0; i < genericArgTypeNames.Length; i++)
            {
                typeArgs[i] = FindType(genericArgTypeNames[i], null)!;
                if (typeArgs[i] == null) return null;
            }
            try { return method.MakeGenericMethod(typeArgs); }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
        }

        static readonly Dictionary<string, string> ClrAliases = new()
        {
            { "int", "Int32" }, { "uint", "UInt32" }, { "long", "Int64" }, { "ulong", "UInt64" },
            { "short", "Int16" }, { "ushort", "UInt16" }, { "byte", "Byte" }, { "sbyte", "SByte" },
            { "float", "Single" }, { "double", "Double" }, { "decimal", "Decimal" },
            { "bool", "Boolean" }, { "char", "Char" }, { "string", "String" }, { "object", "Object" },
        };

        static List<MethodInfo> SafeGetMethodsByName(Type type, string name, BindingFlags bindingFlags)
        {
            try
            {
                return type.GetMethods(bindingFlags).Where(m => m.Name == name).ToList();
            }
            catch { return new List<MethodInfo>(); }
        }

        static List<string> SafeGetMethodNames(Type type, BindingFlags bindingFlags)
        {
            try
            {
                return type.GetMethods(bindingFlags).Select(m => m.Name).Distinct().ToList();
            }
            catch { return new List<string>(); }
        }

        // --- arg coercion -----------------------------------------------------------
        //
        // Adapted (copy) from Unity's InvokeMethodTool.ConvertArgs/ConvertArg. GodotObject handle
        // resolution is omitted (v1 targets nodes via node_path, not handle JSON); primitive + enum
        // coercion is identical.

        sealed class ReflectionArgException : Exception
        {
            internal ReflectionArgException(string message) : base(message) { }
        }

        static object[] ConvertArgs(List<object?>? args, ParameterInfo[] parameters)
        {
            if (args == null || args.Count == 0) return Array.Empty<object>();
            if (parameters.Length == 0) return Array.Empty<object>();

            int count = Math.Min(args.Count, parameters.Length);
            var result = new object[count];
            for (int i = 0; i < count; i++)
                result[i] = ConvertArg(args[i], parameters[i].ParameterType, parameters[i].Name ?? $"arg{i}");
            return result;
        }

        static object ConvertArg(object? value, Type targetType, string paramName)
        {
            if (value == null)
            {
                if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) == null)
                    return Activator.CreateInstance(targetType);
                return null!;
            }

            if (targetType == typeof(string))
                return value is string s ? s : value.ToString()!;
            if (targetType == typeof(int))
                return value is long l ? (int)l : value is double d ? (int)d : Convert.ToInt32(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(long))
                return value is double d2 ? (long)d2 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(float))
                return Convert.ToSingle(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(double))
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(bool))
                return value is bool b ? b : Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(byte))
                return Convert.ToByte(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(short))
                return Convert.ToInt16(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(uint))
                return Convert.ToUInt32(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(ulong))
                return Convert.ToUInt64(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(char))
                return value is string cs && cs.Length == 1 ? cs[0] : Convert.ToChar(value, CultureInfo.InvariantCulture);
            var nullableUnderlying = Nullable.GetUnderlyingType(targetType);
            if (nullableUnderlying != null)
                return ConvertArg(value, nullableUnderlying, paramName);
            if (targetType.IsEnum)
            {
                try { return Enum.Parse(targetType, value.ToString()!, true); }
                catch
                {
                    throw new ReflectionArgException(
                        $"Cannot convert argument '{paramName}' value '{value}' to enum '{targetType.FullName}'. " +
                        $"Valid values: {string.Join(", ", Enum.GetNames(targetType))}.");
                }
            }
            // Fall through: assume the raw value is assignable (the resolved type already accepts it).
            return value;
        }

        // --- return-value serialization ---------------------------------------------
        //
        // Bounded, cycle-safe serialization of the invoked method's return value. Mirrors Unity's
        // OutputSerializer contract (depth cap + per-list item cap + cycle detection) but is
        // greenfield here (no shared OutputSerializer type in the Godot bridge). GodotObject
        // instances are summarized (type + instance id) rather than fully walked — returning a full
        // engine object graph is unsafe and floods context.

        sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }

        static void AppendValue(StringBuilder sb, object? value, int maxDepth, int maxItems, int depth, HashSet<object> visited)
        {
            if (value == null) { sb.Append("null"); return; }

            switch (value)
            {
                case bool b: sb.Append(b ? "true" : "false"); return;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); return;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); return;
                case double d:
                    // JSON has no NaN/Infinity — emit null for those so the payload stays valid.
                    if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
                    AppendDouble(sb, d); return;
                case float f:
                    if (float.IsNaN(f) || float.IsInfinity(f)) { sb.Append("null"); return; }
                    AppendDouble(sb, f); return;
                case decimal dec: sb.Append(dec.ToString(CultureInfo.InvariantCulture)); return;
                case byte by: sb.Append(by.ToString(CultureInfo.InvariantCulture)); return;
                case sbyte sb2: sb.Append(sb2.ToString(CultureInfo.InvariantCulture)); return;
                case short sh: sb.Append(sh.ToString(CultureInfo.InvariantCulture)); return;
                case ushort ush: sb.Append(ush.ToString(CultureInfo.InvariantCulture)); return;
                case uint ui: sb.Append(ui.ToString(CultureInfo.InvariantCulture)); return;
                case ulong ul: sb.Append(ul.ToString(CultureInfo.InvariantCulture)); return;
                case char ch: sb.Append(BridgeJson.EscapeString(ch.ToString())); return;
                case string s2: sb.Append(BridgeJson.EscapeString(s2)); return;
                case Enum e: sb.Append(BridgeJson.EscapeString(e.ToString())); return;
            }

            var type = value.GetType();

            // Cycle + depth guard for reference types.
            if (!type.IsValueType)
            {
                if (depth >= maxDepth)
                {
                    sb.Append(BridgeJson.EscapeString(Summarize(value)));
                    return;
                }
                if (!visited.Add(value))
                {
                    // Already seen — emit a summary to break the cycle.
                    sb.Append("{\"__cycle__\":").Append(BridgeJson.EscapeString(Summarize(value))).Append('}');
                    return;
                }
            }

            if (value is IDictionary dict)
            {
                AppendDictionary(sb, dict, maxDepth, maxItems, depth, visited);
                if (!type.IsValueType) visited.Remove(value);
                return;
            }

            if (value is IEnumerable enumerable && !(value is string))
            {
                AppendEnumerable(sb, enumerable, maxDepth, maxItems, depth, visited);
                if (!type.IsValueType) visited.Remove(value);
                return;
            }

            // GodotObject — summarize, do not walk (engine object graphs are unsafe to serialize).
            if (value is GodotObject godotObj)
            {
                sb.Append("{\"__godot_object__\":").Append(BridgeJson.EscapeString(type.FullName ?? type.Name));
                sb.Append(",\"instance_id\":").Append(godotObj.GetInstanceId().ToString(CultureInfo.InvariantCulture));
                sb.Append('}');
                if (!type.IsValueType) visited.Remove(value);
                return;
            }

            // Plain object — reflect public properties (depth-bounded).
            AppendObject(sb, value, type, maxDepth, maxItems, depth, visited);
            if (!type.IsValueType) visited.Remove(value);
        }

        static void AppendDouble(StringBuilder sb, double d)
        {
            // Use Round-Trip format to preserve precision; trim to invariant culture.
            sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
        }

        static void AppendDictionary(StringBuilder sb, IDictionary dict, int maxDepth, int maxItems, int depth, HashSet<object> visited)
        {
            sb.Append('{');
            bool first = true;
            int emitted = 0;
            int truncated = 0;
            foreach (DictionaryEntry entry in dict)
            {
                if (emitted >= maxItems) { truncated++; continue; }
                if (!first) sb.Append(',');
                first = false;
                sb.Append(BridgeJson.EscapeString(entry.Key?.ToString() ?? string.Empty));
                sb.Append(':');
                AppendValue(sb, entry.Value, maxDepth, maxItems, depth + 1, visited);
                emitted++;
            }
            if (truncated > 0)
            {
                if (!first) sb.Append(',');
                sb.Append("\"__truncated__\":").Append(truncated.ToString(CultureInfo.InvariantCulture));
            }
            sb.Append('}');
        }

        static void AppendEnumerable(StringBuilder sb, IEnumerable enumerable, int maxDepth, int maxItems, int depth, HashSet<object> visited)
        {
            sb.Append('[');
            bool first = true;
            int emitted = 0;
            int truncated = 0;
            foreach (var item in enumerable)
            {
                if (emitted >= maxItems) { truncated++; continue; }
                if (!first) sb.Append(',');
                first = false;
                AppendValue(sb, item, maxDepth, maxItems, depth + 1, visited);
                emitted++;
            }
            if (truncated > 0)
            {
                if (!first) sb.Append(',');
                sb.Append(truncated.ToString(CultureInfo.InvariantCulture));
            }
            sb.Append(']');
        }

        static void AppendObject(StringBuilder sb, object value, Type type, int maxDepth, int maxItems, int depth, HashSet<object> visited)
        {
            PropertyInfo[] props;
            try
            {
                props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            }
            catch { props = Array.Empty<PropertyInfo>(); }

            sb.Append('{');
            bool first = true;
            int emitted = 0;
            foreach (var prop in props)
            {
                // Skip indexers (they require parameters) and unreadable properties.
                if (prop.GetIndexParameters().Length > 0) continue;
                if (!prop.CanRead) continue;
                object? pv;
                try { pv = prop.GetValue(value); }
                catch { continue; }
                if (!first) sb.Append(',');
                first = false;
                sb.Append(BridgeJson.EscapeString(prop.Name));
                sb.Append(':');
                AppendValue(sb, pv, maxDepth, maxItems, depth + 1, visited);
                emitted++;
                if (emitted >= maxItems) break;
            }
            sb.Append('}');
        }

        static string Summarize(object value)
        {
            var t = value.GetType();
            return $"{t.FullName ?? t.Name}@{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value):x}";
        }
    }
}
#endif
