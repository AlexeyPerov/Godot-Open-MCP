#nullable enable
using GodotOpenMcp.Bridge.Editor;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// P4.5 unit tests for the pure-managed editor application-state pieces: the
    /// <see cref="EditorApplicationSetStateBody"/> body parser, the
    /// <see cref="EditorApplicationStateData"/> DTO serialization, and the
    /// <see cref="EditorStateSettleWait"/> bounded observation loop.
    ///
    /// <para>
    /// These mirror the P4.3/P4.4 suites: the units covered here are exactly the ones most prone to
    /// off-by-one / escaping / boolean-parsing bugs, and they are pure-managed so they run in the
    /// binary-less xUnit host (no Godot editor needed). The <c>#if TOOLS</c> handlers
    /// (<see cref="EditorApplicationTools.GetState"/>/<see cref="EditorApplicationTools.SetState"/>)
    /// and their <c>EditorInterface</c> interactions are exercised by the headless Godot smoke and the
    /// gate integration tests (<see cref="EditorApplicationGateTests"/>), not here.
    /// </para>
    /// </summary>
    public class EditorApplicationSetStateBodyTests
    {
        // --- is_playing ----------------------------------------------------------

        [Fact]
        public void Parse_EmptyBody_DefaultsToStop()
        {
            var body = EditorApplicationSetStateBody.Parse(null);
            Assert.False(body.IsPlaying);
            Assert.False(body.HasIsPlayingField);
            Assert.False(body.HasScene);
            Assert.Equal(EditorApplicationSetStateBody.DefaultTimeoutMs, body.TimeoutMs);
        }

        [Fact]
        public void Parse_IsPlayingSnakeCase_True()
        {
            var body = EditorApplicationSetStateBody.Parse("{\"is_playing\":true}");
            Assert.True(body.IsPlaying);
            Assert.True(body.HasIsPlayingField);
        }

        [Fact]
        public void Parse_IsPlayingCamelCase_True()
        {
            var body = EditorApplicationSetStateBody.Parse("{\"isPlaying\":true}");
            Assert.True(body.IsPlaying);
            Assert.True(body.HasIsPlayingField);
        }

        [Fact]
        public void Parse_IsPlayingFalse_Explicit()
        {
            var body = EditorApplicationSetStateBody.Parse("{\"is_playing\":false}");
            Assert.False(body.IsPlaying);
            // Explicit false is still a present field — distinct from absent (default stop).
            Assert.True(body.HasIsPlayingField);
        }

        [Fact]
        public void Parse_IsPlayingNonBool_TreatedAsAbsent()
        {
            // A non-boolean literal is treated as absent → default stop, no field present.
            var body = EditorApplicationSetStateBody.Parse("{\"is_playing\":\"yes\"}");
            Assert.False(body.IsPlaying);
            Assert.False(body.HasIsPlayingField);
        }

        // --- scene selector ------------------------------------------------------

        [Fact]
        public void Parse_SceneSnakeCase_RoundTrips()
        {
            var body = EditorApplicationSetStateBody.Parse("{\"scene\":\"res://main.tscn\"}");
            Assert.True(body.HasScene);
            Assert.Equal("res://main.tscn", body.Scene);
        }

        [Fact]
        public void Parse_SceneCamelCase_RoundTrips()
        {
            var body = EditorApplicationSetStateBody.Parse("{\"scenePath\":\"current\"}");
            Assert.True(body.HasScene);
            Assert.Equal("current", body.Scene);
        }

        [Fact]
        public void Parse_ExplicitNullScene_TreatedAsAbsent()
        {
            var body = EditorApplicationSetStateBody.Parse("{\"scene\":null,\"is_playing\":true}");
            Assert.False(body.HasScene);
            Assert.True(body.IsPlaying);
        }

        [Fact]
        public void Parse_WhitespaceScene_TreatedAsAbsent()
        {
            var body = EditorApplicationSetStateBody.Parse("{\"scene\":\"   \"}");
            Assert.False(body.HasScene);
        }

        [Fact]
        public void Parse_EscapedScene_Unescaped()
        {
            // A scene path with an escaped quote (contrived but pins the escape decoder).
            var body = EditorApplicationSetStateBody.Parse("{\"scene\":\"res://a\\\"b.tscn\"}");
            Assert.Equal("res://a\"b.tscn", body.Scene);
        }

        // --- timeout_ms ----------------------------------------------------------

        [Theory]
        [InlineData("{\"timeout_ms\":2000}", 2000)]
        [InlineData("{\"timeout_ms\":1000}", 1000)]   // min bound
        [InlineData("{\"timeout_ms\":60000}", 60000)] // max bound
        [InlineData("{\"timeout_ms\":500}", 1000)]    // below min → clamped up
        [InlineData("{\"timeout_ms\":99999}", 60000)] // above max → clamped down
        [InlineData("{}", EditorApplicationSetStateBody.DefaultTimeoutMs)]
        public void Parse_TimeoutMs_Clamped(string json, int expected)
        {
            var body = EditorApplicationSetStateBody.Parse(json);
            Assert.Equal(expected, body.TimeoutMs);
        }

        [Fact]
        public void Parse_TimeoutMsCamelCase_RoundTrips()
        {
            var body = EditorApplicationSetStateBody.Parse("{\"timeoutMs\":3000}");
            Assert.Equal(3000, body.TimeoutMs);
        }

        [Fact]
        public void Parse_AllFields_RoundTrip()
        {
            var body = EditorApplicationSetStateBody.Parse(
                "{\"is_playing\":true,\"scene\":\"current\",\"timeout_ms\":4000," +
                "\"paths_hint\":[\"res://project.godot\"],\"gate\":\"enforce\"}");
            Assert.True(body.IsPlaying);
            Assert.True(body.HasScene);
            Assert.Equal("current", body.Scene);
            Assert.Equal(4000, body.TimeoutMs);
        }
    }

    /// <summary>
    /// DTO serialization tests for <see cref="EditorApplicationStateData"/> — pins the fixed field
    /// order and the Godot-adapted shape (no Unity-only <c>isPaused</c>/<c>isCompiling</c>).
    /// </summary>
    public class EditorApplicationStateDataTests
    {
        [Fact]
        public void AppendJsonTo_Stopped_ProducesExpectedShape()
        {
            var state = new EditorApplicationStateData
            {
                IsPlaying = false,
                PlayingScene = null,
                EditorVersion = "4.3.stable.mono",
                ObservedAt = "2026-07-10T19:00:00.000Z",
            };
            var json = state.ToJsonString();
            Assert.Equal(
                "{\"isPlaying\":false,\"playingScene\":null," +
                "\"editorVersion\":\"4.3.stable.mono\"," +
                "\"observedAt\":\"2026-07-10T19:00:00.000Z\"}",
                json);
        }

        [Fact]
        public void AppendJsonTo_Playing_IncludesPlayingScene()
        {
            var state = new EditorApplicationStateData
            {
                IsPlaying = true,
                PlayingScene = "res://main.tscn",
                EditorVersion = "4.3.stable.mono",
                ObservedAt = "2026-07-10T19:00:00.000Z",
            };
            var json = state.ToJsonString();
            Assert.Contains("\"isPlaying\":true", json);
            Assert.Contains("\"playingScene\":\"res://main.tscn\"", json);
        }

        [Fact]
        public void AppendJsonTo_DoesNotExposeUnityOnlyFields()
        {
            // Guards against an accidental copy-paste from Unity's editor_status DTO.
            var state = new EditorApplicationStateData();
            var json = state.ToJsonString();
            Assert.DoesNotContain("isPaused", json);
            Assert.DoesNotContain("isCompiling", json);
            Assert.DoesNotContain("editorType", json);
            Assert.DoesNotContain("currentScene", json);
        }

        [Fact]
        public void NowObservedAt_ProducesIsoUtcShape()
        {
            var stamp = EditorApplicationStateData.NowObservedAt();
            // ISO-8601 UTC with milliseconds and a trailing Z.
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", stamp);
        }
    }

    /// <summary>
    /// Bounded observation loop tests for <see cref="EditorStateSettleWait"/>. Uses an instant sleep
    /// shim so the loop is exercised without wall-clock delay.
    /// </summary>
    public class EditorStateSettleWaitTests
    {
        [Fact]
        public void Wait_AlreadyMatches_ShortCircuits()
        {
            // Predicate true on the first check → settled immediately, zero elapsed.
            var (settled, elapsedMs) = EditorStateSettleWait.Wait(
                () => true, timeoutMs: 5000, sleep: _ => { });
            Assert.True(settled);
            Assert.Equal(0, elapsedMs);
        }

        [Fact]
        public void Wait_MatchesAfterTicks_ReturnsSettled()
        {
            // Match after 3 ticks (300ms at TickMs=100).
            int ticks = 0;
            var (settled, elapsedMs) = EditorStateSettleWait.Wait(
                () => ++ticks >= 4, timeoutMs: 5000, sleep: _ => { });
            Assert.True(settled);
            Assert.Equal(300, elapsedMs);
        }

        [Fact]
        public void Wait_NeverMatches_TimesOutBounded()
        {
            var (settled, elapsedMs) = EditorStateSettleWait.Wait(
                () => false, timeoutMs: 500, sleep: _ => { });
            Assert.False(settled);
            // Elapsed is a multiple of TickMs that reaches the budget.
            Assert.True(elapsedMs >= 500, $"elapsed {elapsedMs} should reach the timeout budget");
        }

        [Fact]
        public void Wait_NonPositiveTimeout_FlooredToTick()
        {
            // A non-positive timeout is floored to one tick so the predicate is still checked once.
            var (settled, _) = EditorStateSettleWait.Wait(
                () => true, timeoutMs: 0, sleep: _ => { });
            Assert.True(settled);
        }

        [Fact]
        public void Wait_NullPredicate_Throws()
        {
            // The handler contract: a null predicate is a programming error, not a recoverable state.
            Assert.Throws<System.ArgumentNullException>(() =>
                EditorStateSettleWait.Wait(null!, timeoutMs: 100, sleep: _ => { }));
        }
    }
}
