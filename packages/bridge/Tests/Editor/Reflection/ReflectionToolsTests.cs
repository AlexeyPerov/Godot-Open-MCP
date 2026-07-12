#nullable enable
using System;
using GodotOpenMcp.Bridge.Editor;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// P5.1 unit tests for the pure-managed reflection body parsers
    /// (<see cref="ReflectionMethodFindBody"/> + <see cref="ReflectionMethodCallBody"/>). These mirror
    /// the P2.2 <c>NodeFindBodyTests</c> / P4.3 <c>ResourceMoveBodyTests</c> suites: the units covered
    /// here are exactly the ones most prone to off-by-one / escaping / defaulting bugs, and they are
    /// pure-managed so they run in the binary-less xUnit host (no Godot editor needed).
    ///
    /// <para>
    /// The <c>#if TOOLS</c> handlers (<c>ReflectionTools.Find</c>/<c>Call</c> — AppDomain /
    /// EditorInterface / Godot.Object-coupled) are exercised by the headless Godot smoke and the gate
    /// integration tests (which stub the gate and the handler, exercising only the dispatch routing +
    /// paths_hint guard), not here.
    /// </para>
    ///
    /// <para>
    /// Parity vs Unity Open MCP: the find-body assertions mirror the <c>find_members</c> schema pins
    /// (query/kind/assembly_filter/include_signatures/max_results), with <c>include_godot_editor</c>
    /// replacing Unity's <c>include_unity_editor</c>; the call-body assertions mirror
    /// <c>invoke_method</c> (type_name/method_name/args/arg_type_names/generic_arg_types/is_static/
    /// assembly_name/max_depth/max_items), with Godot-native <c>node_path</c> + <c>object_id</c>
    /// targeting and an <c>execute_in_main_thread</c> echo.
    /// </para>
    /// </summary>
    public class ReflectionMethodFindBodyTests
    {
        // --- defaults / empty body ---

        [Fact]
        public void Parse_EmptyBody_AllDefaults()
        {
            var body = ReflectionMethodFindBody.Parse(null);
            Assert.Equal(string.Empty, body.Query);
            Assert.Equal("all", body.Kind);
            Assert.Null(body.AssemblyFilter);
            Assert.True(body.IncludeGodotEditor);
            Assert.True(body.IncludeProject);
            Assert.True(body.IncludeSignatures);
            Assert.Null(body.TypeName);
            Assert.Equal(ReflectionMethodFindBody.DefaultMaxResults, body.MaxResults);
            Assert.True(body.WantsTypes);
            Assert.True(body.WantsMethods);
            Assert.True(body.WantsProperties);
        }

        [Fact]
        public void Parse_EmptyJsonObject_AllDefaults()
        {
            var body = ReflectionMethodFindBody.Parse("{}");
            Assert.Equal("all", body.Kind);
            Assert.Equal(ReflectionMethodFindBody.DefaultMaxResults, body.MaxResults);
        }

        // --- scalar fields round-trip ---

        [Fact]
        public void Parse_Query_RoundTrips()
        {
            var body = ReflectionMethodFindBody.Parse("{\"query\":\"GetNode\"}");
            Assert.Equal("GetNode", body.Query);
        }

        [Fact]
        public void Parse_QueryWithEscapes_RoundTrips()
        {
            // \" \\ \n \t \/ \uXXXX — the standard JSON escape set the slicer honors.
            var body = ReflectionMethodFindBody.Parse("{\"query\":\"a\\\"b\\\\c\\nd\\te\\/f\\u005Ag\"}");
            Assert.Equal("a\"b\\c\nd\te/fZg", body.Query);
        }

        [Theory]
        [InlineData("type", "type")]
        [InlineData("method", "method")]
        [InlineData("property", "property")]
        [InlineData("all", "all")]
        [InlineData("ALL", "all")] // case-insensitive normalization
        [InlineData("Method", "method")]
        [InlineData("bogus", "all")] // unknown → all
        [InlineData("", "all")] // empty → default (all)
        public void Parse_Kind_Normalizes(string input, string expected)
        {
            var body = ReflectionMethodFindBody.Parse($"{{\"kind\":\"{input}\"}}");
            Assert.Equal(expected, body.Kind);
        }

        [Fact]
        public void Parse_KindUpdatesWantFlags()
        {
            var body = ReflectionMethodFindBody.Parse("{\"kind\":\"method\"}");
            Assert.False(body.WantsTypes);
            Assert.True(body.WantsMethods);
            Assert.False(body.WantsProperties);
        }

        [Fact]
        public void Parse_AssemblyFilter_RoundTrips()
        {
            var body = ReflectionMethodFindBody.Parse("{\"assembly_filter\":\"GodotSharp\"}");
            Assert.Equal("GodotSharp", body.AssemblyFilter);
        }

        [Fact]
        public void Parse_IncludeFlags_BooleansRoundTrip()
        {
            var body = ReflectionMethodFindBody.Parse(
                "{\"include_godot_editor\":false,\"include_project\":false,\"include_signatures\":false}");
            Assert.False(body.IncludeGodotEditor);
            Assert.False(body.IncludeProject);
            Assert.False(body.IncludeSignatures);
        }

        [Fact]
        public void Parse_ExplicitNull_TreatedAsAbsent()
        {
            // null literals → defaults (true for the include flags).
            var body = ReflectionMethodFindBody.Parse(
                "{\"include_godot_editor\":null,\"include_project\":null,\"include_signatures\":null,\"query\":null,\"kind\":null,\"assembly_filter\":null,\"type_name\":null}");
            Assert.True(body.IncludeGodotEditor);
            Assert.True(body.IncludeProject);
            Assert.True(body.IncludeSignatures);
            Assert.Equal(string.Empty, body.Query);
            Assert.Equal("all", body.Kind);
            Assert.Null(body.AssemblyFilter);
            Assert.Null(body.TypeName);
        }

        [Fact]
        public void Parse_TypeName_RoundTrips()
        {
            var body = ReflectionMethodFindBody.Parse("{\"type_name\":\"Godot.Node\"}");
            Assert.Equal("Godot.Node", body.TypeName);
        }

        // --- max_results clamping ---

        [Fact]
        public void Parse_MaxResults_DefaultIsFifty()
        {
            var body = ReflectionMethodFindBody.Parse("{}");
            Assert.Equal(50, body.MaxResults);
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(50, 50)]
        [InlineData(200, 200)]
        [InlineData(0, 50)] // non-positive → default
        [InlineData(-5, 50)]
        [InlineData(201, 200)] // over hard max → clamped
        [InlineData(99999, 200)]
        public void Parse_MaxResults_Clamped(int input, int expected)
        {
            var body = ReflectionMethodFindBody.Parse($"{{\"max_results\":{input}}}");
            Assert.Equal(expected, body.MaxResults);
        }

        [Fact]
        public void Parse_MaxResults_MalformedFallsBackToDefault()
        {
            var body = ReflectionMethodFindBody.Parse("{\"max_results\":\"not-a-number\"}");
            Assert.Equal(ReflectionMethodFindBody.DefaultMaxResults, body.MaxResults);
        }

        // --- full-body round-trip ---

        [Fact]
        public void Parse_FullBody_AllFieldsRoundTrip()
        {
            var json = "{\"query\":\"AddChild\",\"kind\":\"method\",\"assembly_filter\":\"GodotSharp\"," +
                       "\"include_godot_editor\":true,\"include_project\":false,\"include_signatures\":true," +
                       "\"type_name\":\"Godot.Node\",\"max_results\":25}";
            var body = ReflectionMethodFindBody.Parse(json);
            Assert.Equal("AddChild", body.Query);
            Assert.Equal("method", body.Kind);
            Assert.Equal("GodotSharp", body.AssemblyFilter);
            Assert.True(body.IncludeGodotEditor);
            Assert.False(body.IncludeProject);
            Assert.True(body.IncludeSignatures);
            Assert.Equal("Godot.Node", body.TypeName);
            Assert.Equal(25, body.MaxResults);
        }

        // --- key-substring confusion guard ---

        [Fact]
        public void Parse_QueryValueContainingKeySubstring_NotConfused()
        {
            // A value that itself contains the text "query" must not re-trigger the scanner.
            var body = ReflectionMethodFindBody.Parse("{\"query\":\"the query field\"}");
            Assert.Equal("the query field", body.Query);
        }
    }

    public class ReflectionMethodCallBodyTests
    {
        // --- required fields + defaults ---

        [Fact]
        public void Parse_EmptyBody_AllDefaults()
        {
            var body = ReflectionMethodCallBody.Parse(null);
            Assert.Equal(string.Empty, body.TypeName);
            Assert.Equal(string.Empty, body.MethodName);
            Assert.False(body.IsStatic);
            Assert.Null(body.Args);
            Assert.False(body.HasArgs);
            Assert.Null(body.ArgTypeNames);
            Assert.Null(body.GenericArgTypes);
            Assert.Null(body.AssemblyName);
            Assert.Equal(string.Empty, body.NodePath);
            Assert.Equal(0, body.ObjectId);
            Assert.True(body.ExecuteInMainThread);
            Assert.Equal(4, body.MaxDepth);
            Assert.Equal(100, body.MaxItems);
            Assert.False(body.HasInstanceTarget);
        }

        // --- scalar fields round-trip ---

        [Fact]
        public void Parse_TypeAndMethod_RoundTrip()
        {
            var body = ReflectionMethodCallBody.Parse(
                "{\"type_name\":\"Godot.Node\",\"method_name\":\"GetChildCount\"}");
            Assert.Equal("Godot.Node", body.TypeName);
            Assert.Equal("GetChildCount", body.MethodName);
        }

        [Fact]
        public void Parse_IsStatic_RoundTrips()
        {
            var body = ReflectionMethodCallBody.Parse(
                "{\"type_name\":\"System.Math\",\"method_name\":\"Abs\",\"is_static\":true}");
            Assert.True(body.IsStatic);
        }

        [Fact]
        public void Parse_AssemblyName_RoundTrips()
        {
            var body = ReflectionMethodCallBody.Parse(
                "{\"type_name\":\"Node\",\"method_name\":\"M\",\"assembly_name\":\"GodotSharp\"}");
            Assert.Equal("GodotSharp", body.AssemblyName);
        }

        [Fact]
        public void Parse_NodePath_RoundTrips()
        {
            var body = ReflectionMethodCallBody.Parse(
                "{\"type_name\":\"Godot.Node\",\"method_name\":\"M\",\"node_path\":\"Main/Player\"}");
            Assert.Equal("Main/Player", body.NodePath);
            Assert.True(body.HasInstanceTarget);
        }

        [Fact]
        public void Parse_ObjectId_RoundTrips()
        {
            var body = ReflectionMethodCallBody.Parse(
                "{\"type_name\":\"Godot.Node\",\"method_name\":\"M\",\"object_id\":12345}");
            Assert.Equal(12345, body.ObjectId);
            Assert.True(body.HasInstanceTarget);
        }

        [Fact]
        public void Parse_ExecuteInMainThread_RoundTrips()
        {
            var body = ReflectionMethodCallBody.Parse(
                "{\"type_name\":\"T\",\"method_name\":\"M\",\"execute_in_main_thread\":false}");
            Assert.False(body.ExecuteInMainThread);
        }

        [Fact]
        public void Parse_ExplicitNull_TreatedAsAbsent()
        {
            var body = ReflectionMethodCallBody.Parse(
                "{\"type_name\":null,\"method_name\":null,\"assembly_name\":null,\"node_path\":null,\"args\":null,\"arg_type_names\":null,\"generic_arg_types\":null,\"is_static\":null,\"execute_in_main_thread\":null,\"object_id\":null}");
            Assert.Equal(string.Empty, body.TypeName);
            Assert.Equal(string.Empty, body.MethodName);
            Assert.Null(body.AssemblyName);
            Assert.Equal(string.Empty, body.NodePath);
            Assert.Null(body.Args);
            Assert.Null(body.ArgTypeNames);
            Assert.Null(body.GenericArgTypes);
            Assert.False(body.IsStatic);
            Assert.True(body.ExecuteInMainThread); // default
            Assert.Equal(0, body.ObjectId);
        }

        // --- depth / items clamping ---

        [Theory]
        [InlineData(4, 4)]
        [InlineData(10, 10)]
        [InlineData(-1, 4)] // non-positive → default
        [InlineData(0, 4)] // non-positive → default (matches Unity invoke_method max_depth ≤ 0 → 4)
        public void Parse_MaxDepth_Clamped(int input, int expected)
        {
            var body = ReflectionMethodCallBody.Parse($"{{\"type_name\":\"T\",\"method_name\":\"M\",\"max_depth\":{input}}}");
            Assert.Equal(expected, body.MaxDepth);
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(100, 100)]
        [InlineData(500, 500)]
        [InlineData(-1, 100)] // negative → default
        public void Parse_MaxItems_Clamped(int input, int expected)
        {
            var body = ReflectionMethodCallBody.Parse($"{{\"type_name\":\"T\",\"method_name\":\"M\",\"max_items\":{input}}}");
            Assert.Equal(expected, body.MaxItems);
        }

        // --- string arrays ---

        [Fact]
        public void Parse_ArgTypeNames_RoundTrips()
        {
            var body = ReflectionMethodCallBody.Parse(
                "{\"type_name\":\"T\",\"method_name\":\"M\",\"arg_type_names\":[\"Godot.Node\",\"Int32\"]}");
            Assert.NotNull(body.ArgTypeNames);
            Assert.Equal(new[] { "Godot.Node", "Int32" }, body.ArgTypeNames);
        }

        [Fact]
        public void Parse_GenericArgTypes_RoundTrips()
        {
            var body = ReflectionMethodCallBody.Parse(
                "{\"type_name\":\"T\",\"method_name\":\"M\",\"generic_arg_types\":[\"Godot.Node\"]}");
            Assert.NotNull(body.GenericArgTypes);
            Assert.Equal(new[] { "Godot.Node" }, body.GenericArgTypes);
        }

        [Fact]
        public void Parse_EmptyStringArray_RoundTripsAsEmptyNotNull()
        {
            var body = ReflectionMethodCallBody.Parse(
                "{\"type_name\":\"T\",\"method_name\":\"M\",\"arg_type_names\":[]}");
            Assert.NotNull(body.ArgTypeNames);
            Assert.Empty(body.ArgTypeNames);
        }

        [Fact]
        public void Parse_StringArrayWithEscapes_RoundTrips()
        {
            var body = ReflectionMethodCallBody.Parse(
                "{\"type_name\":\"T\",\"method_name\":\"M\",\"arg_type_names\":[\"a\\\"b\",\"c\\\\d\"]}");
            Assert.Equal(new[] { "a\"b", "c\\d" }, body.ArgTypeNames);
        }

        // --- args array (mixed JSON values) ---

        [Fact]
        public void Parse_EmptyArgsArray_HasArgsTrueEmptyList()
        {
            var body = ReflectionMethodCallBody.Parse(
                "{\"type_name\":\"T\",\"method_name\":\"M\",\"args\":[]}");
            Assert.True(body.HasArgs);
            Assert.NotNull(body.Args);
            Assert.Empty(body.Args);
        }

        [Fact]
        public void Parse_NoArgsField_HasArgsFalseNull()
        {
            var body = ReflectionMethodCallBody.Parse("{\"type_name\":\"T\",\"method_name\":\"M\"}");
            Assert.False(body.HasArgs);
            Assert.Null(body.Args);
        }

        [Fact]
        public void Parse_NullArgs_TreatedAsAbsent()
        {
            var body = ReflectionMethodCallBody.Parse(
                "{\"type_name\":\"T\",\"method_name\":\"M\",\"args\":null}");
            Assert.False(body.HasArgs);
            Assert.Null(body.Args);
        }

        [Fact]
        public void Parse_ArgsPrimitives_CoercedToClrTypes()
        {
            var body = ReflectionMethodCallBody.Parse(
                "{\"type_name\":\"T\",\"method_name\":\"M\",\"args\":[42,3.14,\"hello\",true,null,false]}");
            Assert.True(body.HasArgs);
            Assert.NotNull(body.Args);
            Assert.Equal(6, body.Args!.Count);
            Assert.IsType<long>(body.Args[0]);
            Assert.Equal(42L, body.Args[0]);
            Assert.IsType<double>(body.Args[1]);
            Assert.Equal(3.14, body.Args[1]);
            Assert.Equal("hello", body.Args[2]);
            Assert.IsType<bool>(body.Args[3]);
            Assert.Equal(true, body.Args[3]);
            Assert.Null(body.Args[4]);
            Assert.IsType<bool>(body.Args[5]);
            Assert.Equal(false, body.Args[5]);
        }

        [Fact]
        public void Parse_ArgsNegativeInteger_ParsedAsLong()
        {
            var body = ReflectionMethodCallBody.Parse(
                "{\"type_name\":\"T\",\"method_name\":\"M\",\"args\":[-7,-1.5]}");
            Assert.NotNull(body.Args);
            Assert.Equal(-7L, body.Args![0]);
            Assert.Equal(-1.5, body.Args[1]);
        }

        [Fact]
        public void Parse_ArgsStringWithEscapes_RoundTrips()
        {
            var body = ReflectionMethodCallBody.Parse(
                "{\"type_name\":\"T\",\"method_name\":\"M\",\"args\":[\"line1\\nline2\\ttab\"]}");
            Assert.Equal("line1\nline2\ttab", body.Args![0]);
        }

        [Fact]
        public void Parse_ArgsNestedObject_PreservedAsRawJson()
        {
            var body = ReflectionMethodCallBody.Parse(
                "{\"type_name\":\"T\",\"method_name\":\"M\",\"args\":[{\"key\":\"value\",\"n\":1}]}");
            // Nested objects are preserved as their raw JSON substring in v1.
            var raw = body.Args![0] as string;
            Assert.NotNull(raw);
            Assert.Contains("\"key\":\"value\"", raw);
            Assert.Contains("\"n\":1", raw);
        }

        [Fact]
        public void Parse_ArgsNestedArray_PreservedAsRawJson()
        {
            var body = ReflectionMethodCallBody.Parse(
                "{\"type_name\":\"T\",\"method_name\":\"M\",\"args\":[[1,2,3]]}");
            var raw = body.Args![0] as string;
            Assert.NotNull(raw);
            Assert.Equal("[1,2,3]", raw);
        }

        [Fact]
        public void Parse_ArgsObjectWithEscapedQuoteInsideString_BalancedCorrectly()
        {
            // The balanced-brace slicer must honor the escaped quote so the object ends at the right
            // brace (not at the embedded \").
            var body = ReflectionMethodCallBody.Parse(
                "{\"type_name\":\"T\",\"method_name\":\"M\",\"args\":[{\"k\":\"a\\\"b\"},42]}");
            Assert.Equal(2, body.Args!.Count);
            var raw = body.Args[0] as string;
            Assert.NotNull(raw);
            Assert.EndsWith("}", raw);
            Assert.Equal(42L, body.Args[1]);
        }

        // --- full-body round-trip ---

        [Fact]
        public void Parse_FullBody_AllFieldsRoundTrip()
        {
            var json = "{\"type_name\":\"Godot.Node\",\"method_name\":\"MoveChild\"," +
                       "\"args\":[\"ChildName\",1],\"arg_type_names\":[\"Godot.Node\",\"Int32\"]," +
                       "\"generic_arg_types\":[],\"is_static\":false,\"assembly_name\":\"GodotSharp\"," +
                       "\"node_path\":\"/root/Main\",\"object_id\":0,\"execute_in_main_thread\":true," +
                       "\"max_depth\":6,\"max_items\":50,\"paths_hint\":[\"res://main.tscn\"],\"gate\":\"enforce\"}";
            var body = ReflectionMethodCallBody.Parse(json);
            Assert.Equal("Godot.Node", body.TypeName);
            Assert.Equal("MoveChild", body.MethodName);
            Assert.True(body.HasArgs);
            Assert.Equal(2, body.Args!.Count);
            Assert.Equal("ChildName", body.Args[0]);
            Assert.Equal(1L, body.Args[1]);
            Assert.Equal(new[] { "Godot.Node", "Int32" }, body.ArgTypeNames);
            Assert.NotNull(body.GenericArgTypes);
            Assert.Empty(body.GenericArgTypes);
            Assert.False(body.IsStatic);
            Assert.Equal("GodotSharp", body.AssemblyName);
            Assert.Equal("/root/Main", body.NodePath);
            Assert.True(body.HasInstanceTarget);
            Assert.Equal(0, body.ObjectId);
            Assert.True(body.ExecuteInMainThread);
            Assert.Equal(6, body.MaxDepth);
            Assert.Equal(50, body.MaxItems);
        }
    }
}
