#nullable enable
using System;
using System.Diagnostics;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Bounded observation loop for editor play-process state transitions (P4.5). Polls a truth
    /// predicate (e.g. <c>EditorInterface.IsPlayingScene</c>) until it matches the requested state or
    /// the deadline elapses — never blocks indefinitely, never claims a state it did not observe.
    ///
    /// <para>
    /// Adapted (copy fidelity) from Unity Open MCP's <c>EditorSettleWait</c>: the same bounded
    /// poll-with-tick-interval + configurable-cap + warn-on-timeout + short-circuit-on-match
    /// discipline. Unity polls <c>IsCompiling</c> after a domain reload; Godot polls
    /// <c>IsPlayingScene()</c> after a play start/stop because the play process launches
    /// asynchronously (separate OS process). The pattern — "the requested transition may take a tick
    /// to be observable, observe with a bounded deadline, return both requested and observed" — is
    /// identical.
    /// </para>
    ///
    /// <para>
    /// Pure-managed (no Godot API surface, no <c>#if TOOLS</c>): the caller injects the truth predicate
    /// and the sleep shim, so the loop logic is unit-testable in the binary-less xUnit host. The
    /// on-editor handler (<see cref="EditorApplicationTools"/>) supplies
    /// <c>() => EditorInterface.Singleton.IsPlayingScene()</c> and <c>OS.DelayMsec</c>.
    /// </para>
    /// </summary>
    internal static class EditorStateSettleWait
    {
        /// <summary>Poll interval between predicate checks. Mirrors Unity's
        /// <c>EditorSettleWait.TickMs</c>.</summary>
        internal const int TickMs = 100;

        /// <summary>
        /// Wait until <paramref name="matchesRequested"/> returns true, or <paramref name="timeoutMs"/>
        /// elapses. Returns the elapsed milliseconds and whether the requested state was observed.
        ///
        /// <para>
        /// The sleep shim (<paramref name="sleep"/>) defaults to <see cref="System.Threading.Thread.Sleep"/>
        /// so production callers can omit it; tests inject an instant sleep so the test does not block on
        /// wall-clock time. The predicate is re-evaluated every tick.
        /// </para>
        /// </summary>
        /// <param name="matchesRequested">Predicate that returns true when the requested state is
        /// observed (e.g. <c>() => EditorInterface.Singleton.IsPlayingScene()</c> after a start
        /// request).</param>
        /// <param name="timeoutMs">Maximum total wait. Bounded by the caller (clamped in
        /// <see cref="EditorApplicationSetStateBody"/>).</param>
        /// <param name="sleep">Optional sleep shim (defaults to <see cref="System.Threading.Thread.Sleep(int)"/>).
        /// Tests pass a no-op so the loop is instant.</param>
        /// <returns>A (<see cref="Settled"/>, <see cref="ElapsedMs"/>) tuple. <see cref="Settled"/> is
        /// true when the predicate matched before the deadline.</returns>
        internal static (bool Settled, int ElapsedMs) Wait(
            Func<bool> matchesRequested,
            int timeoutMs,
            Action<int>? sleep = null)
        {
            if (matchesRequested == null)
                throw new ArgumentNullException(nameof(matchesRequested));
            if (timeoutMs <= 0)
                timeoutMs = TickMs;

            var sleeper = sleep ?? DefaultSleep;
            var sw = Stopwatch.StartNew();

            // Check the predicate FIRST — the transition may already have landed synchronously (Unity's
            // settle wait does the same: short-circuit before the first tick).
            if (matchesRequested())
            {
                sw.Stop();
                return (Settled: true, ElapsedMs: (int)sw.ElapsedMilliseconds);
            }

            var elapsed = 0;
            while (elapsed < timeoutMs)
            {
                sleeper(TickMs);
                elapsed += TickMs;
                if (matchesRequested())
                    return (Settled: true, ElapsedMs: elapsed);
            }

            return (Settled: false, ElapsedMs: elapsed);
        }

        static void DefaultSleep(int ms) => System.Threading.Thread.Sleep(ms);
    }
}
