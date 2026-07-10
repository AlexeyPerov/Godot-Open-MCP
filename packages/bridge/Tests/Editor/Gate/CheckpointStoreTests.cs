#nullable enable
using System.Collections.Generic;
using GodotOpenMcp.Bridge.Editor;
using GodotOpenMcp.Verify.Core;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// Tests <see cref="CheckpointStore"/> — the session-scoped in-memory store the P3.6
    /// <c>checkpoint_create</c> / <c>delta</c> meta-tools use to thread a baseline across separate
    /// tool calls. Pure-managed Dictionary/list math, no Godot APIs — unit-tested directly here.
    ///
    /// <para>
    /// Ported (copy for the test shape) from Unity Open MCP's <c>CheckpointStore</c> behavior; the
    /// cases pin the load-bearing contracts: overwrite-on-duplicate, LRU eviction under capacity
    /// pressure, access-clock refresh on Get, and null/unknown-id lookups.
    /// </para>
    /// </para>
    /// </summary>
    /// <summary>
    /// Tests share the process-static <see cref="CheckpointStore"/> with <see cref="GateMetaToolsTests"/>
    /// (which also clears/stores entries). The <c>[Collection]</c> attribute serializes the two classes
    /// against each other so the LRU-ordering assertions (e.g. <c>Get_RefreshesAccessClock</c>) never
    /// observe mid-mutation state from a concurrent class.
    /// </summary>
    [Collection("CheckpointStore")]
    [CollectionDefinition("CheckpointStore", DisableParallelization = true)]
    public class CheckpointStoreTests
    {
        public CheckpointStoreTests()
        {
            // Each test starts from an empty store — the store is process-static, so Clear() in the
            // ctor isolates tests from each other and from any state leaked by a prior test.
            CheckpointStore.Clear();
        }

        // --- Store + Get ---------------------------------------------------------

        [Fact]
        public void Store_ThenGet_ReturnsEntry()
        {
            var entry = Entry("cp_abc123");
            CheckpointStore.Store(entry);

            var got = CheckpointStore.Get("cp_abc123");
            Assert.NotNull(got);
            Assert.Same(entry, got);
            Assert.Equal(1, CheckpointStore.Count);
        }

        [Fact]
        public void Get_UnknownId_ReturnsNull()
        {
            // The load-bearing delta-tool contract: a missing checkpoint returns null (not an
            // exception), and DeltaTool turns that into the structured `unavailable` payload.
            Assert.Null(CheckpointStore.Get("cp_does_not_exist"));
        }

        [Fact]
        public void Get_NullId_ReturnsNull()
        {
            Assert.Null(CheckpointStore.Get(null!));
        }

        // --- overwrite on duplicate id ------------------------------------------

        [Fact]
        public void Store_DuplicateId_OverwritesLatestWins()
        {
            // A re-submission of the same checkpoint id must replace the prior entry, not be a
            // silent no-op — otherwise a freshly re-captured fingerprint would be discarded.
            var first = Entry("cp_dup", label: "first");
            var second = Entry("cp_dup", label: "second");
            CheckpointStore.Store(first);
            CheckpointStore.Store(second);

            Assert.Equal(1, CheckpointStore.Count);
            var got = CheckpointStore.Get("cp_dup");
            Assert.NotNull(got);
            Assert.Equal("second", got!.Label);
        }

        // --- LRU eviction --------------------------------------------------------

        [Fact]
        public void Store_OverCapacity_EvictsLeastRecentlyAccessed()
        {
            // Fill to capacity. Each Store bumps Count; crossing DefaultCapacity triggers LRU
            // eviction of the entry with the oldest LastAccessedUtc.
            for (int i = 0; i < CheckpointStore.DefaultCapacity; i++)
                CheckpointStore.Store(Entry($"cp_{i:D2}"));
            Assert.Equal(CheckpointStore.DefaultCapacity, CheckpointStore.Count);

            // Touch cp_00 so its access clock is newer than the rest — it must survive eviction.
            CheckpointStore.Get("cp_00");

            // Insert one more → evicts the oldest-accessed (cp_01, never touched after Store).
            CheckpointStore.Store(Entry("cp_new"));
            Assert.Equal(CheckpointStore.DefaultCapacity, CheckpointStore.Count);
            Assert.NotNull(CheckpointStore.Get("cp_00"));   // touched → survived
            Assert.Null(CheckpointStore.Get("cp_01"));      // oldest untouched → evicted
            Assert.NotNull(CheckpointStore.Get("cp_new"));  // just inserted → present
        }

        [Fact]
        public void Get_RefreshesAccessClock()
        {
            // cp_old is stored first (oldest access clock); cp_newer stored after. Without a Get on
            // cp_old, inserting a third would evict cp_old. But Get refreshes cp_old's clock so it
            // survives.
            CheckpointStore.Store(Entry("cp_old", accessed: "2026-01-01T00:00:00Z"));
            CheckpointStore.Store(Entry("cp_newer", accessed: "2026-06-01T00:00:00Z"));

            CheckpointStore.Get("cp_old"); // bumps LastAccessedUtc to ~now

            // Fill the rest to capacity + 1, evicting the now-oldest (cp_newer, never re-touched).
            for (int i = 0; i < CheckpointStore.DefaultCapacity - 1; i++)
                CheckpointStore.Store(Entry($"cp_fill_{i:D2}", accessed: "2026-07-01T00:00:00Z"));

            Assert.NotNull(CheckpointStore.Get("cp_old"));    // Get refreshed it → survived
        }

        // --- Clear ----------------------------------------------------------------

        [Fact]
        public void Clear_EmptyStore()
        {
            CheckpointStore.Store(Entry("cp_a"));
            CheckpointStore.Store(Entry("cp_b"));
            Assert.Equal(2, CheckpointStore.Count);

            CheckpointStore.Clear();
            Assert.Equal(0, CheckpointStore.Count);
            Assert.Null(CheckpointStore.Get("cp_a"));
        }

        // --- helpers --------------------------------------------------------------

        /// <summary>
        /// Build a minimal <see cref="CheckpointStoreEntry"/> with an empty fingerprint. The
        /// fingerprint's contents are irrelevant to the store's behavior — only the id, label, and
        /// access clock matter — so an empty-fingerprint entry is sufficient.
        /// </summary>
        static CheckpointStoreEntry Entry(string id, string? label = null, string? accessed = null)
        {
            var fp = new CheckpointFingerprint(id, new Dictionary<string, RuleFingerprint>());
            return new CheckpointStoreEntry
            {
                CheckpointId = id,
                Timestamp = accessed ?? "2026-07-01T00:00:00Z",
                LastAccessedUtc = accessed ?? "2026-07-01T00:00:00Z",
                Label = label,
                Paths = new[] { "res://Main.tscn" },
                Categories = new[] { "broken_references" },
                Fingerprint = fp,
            };
        }
    }
}
