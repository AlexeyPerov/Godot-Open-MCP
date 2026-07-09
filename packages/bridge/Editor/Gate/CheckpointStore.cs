#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using GodotOpenMcp.Verify.Core;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// A stored checkpoint entry: the id, capture timestamp + access clock, caller label, the
    /// <c>res://</c> paths and rule categories it covered, and the <see cref="CheckpointFingerprint"/>
    /// itself. Ported (copy) from Unity Open MCP's <c>CheckpointStoreEntry</c>; the
    /// <see cref="LastAccessedUtc"/> access clock is kept so the LRU eviction in
    /// <see cref="CheckpointStore"/> keeps actively-delta'd checkpoints alive under insert pressure.
    /// </summary>
    public sealed class CheckpointStoreEntry
    {
        /// <summary>Short opaque id (<c>cp_&lt;6 hex&gt;</c>) — the key the <c>delta</c> tool resumes on.</summary>
        public string CheckpointId = null!;

        /// <summary>ISO-8601 UTC capture timestamp.</summary>
        public string Timestamp = null!;

        /// <summary>
        /// LRU access clock. Seeded from <see cref="Timestamp"/> on <see cref="CheckpointStore.Store"/>;
        /// refreshed on every <see cref="CheckpointStore.Get"/> so a checkpoint an agent is actively
        /// delta-comparing against is not evicted purely by insert count. ISO-8601 UTC so string
        /// comparison is a valid ordering.
        /// </summary>
        public string LastAccessedUtc = null!;

        /// <summary>Caller-supplied label (may be null) — for logging / UI, not identity.</summary>
        public string? Label;

        /// <summary>The <c>res://</c> paths the checkpoint covers; <c>delta</c> defaults to these.</summary>
        public string[]? Paths;

        /// <summary>The rule ids that contributed fingerprints; <c>delta</c> re-validates these.</summary>
        public string[] Categories = null!;

        /// <summary>The before-state fingerprint <c>delta</c> compares the current scan against.</summary>
        public CheckpointFingerprint Fingerprint = null!;
    }

    /// <summary>
    /// Session-scoped in-memory store for <see cref="CheckpointStoreEntry"/>. The bridge writes a
    /// checkpoint here on <c>godot_open_mcp_checkpoint_create</c> and reads it back on
    /// <c>godot_open_mcp_delta</c>, so an agent can run the explicit checkpoint → mutate → delta
    /// workflow in one session. Ported (copy) from Unity Open MCP's <c>CheckpointStore</c>.
    ///
    /// <para>
    /// <b>Session scope.</b> The store is process-static and is wiped on editor restart or an assembly
    /// reload (Godot recompiles the addon assembly on script change). A <c>delta</c> call against an
    /// id that is no longer in the store therefore returns a structured <c>unavailable</c> payload
    /// (see <c>DeltaTool.BuildUnavailableResult</c>), NOT a hard error — the agent falls back to
    /// <c>validate_edit</c> for a direct current-state check.
    /// </para>
    ///
    /// <para>
    /// <b>Capacity + eviction.</b> Bounded to <see cref="DefaultCapacity"/> entries (20). When full,
    /// the entry with the oldest <see cref="CheckpointStoreEntry.LastAccessedUtc"/> is evicted (LRU,
    /// not FIFO) so an agent comparing against a baseline checkpoint is not bumped purely because many
    /// newer gate-run checkpoints arrived. A duplicate <see cref="CheckpointStoreEntry.CheckpointId"/>
    /// on <see cref="Store"/> overwrites — latest data wins — so a re-captured fingerprint replaces the
    /// stale one instead of being silently dropped.
    /// </para>
    ///
    /// <para>
    /// P3.6 is the phase <see cref="GatePolicy"/>'s class doc referenced as "a later phase" for the
    /// store: the gate's own checkpoint→mutate→delta cycle does NOT consult this store (it keeps its
    /// fingerprint on the stack for one dispatch); only the explicit meta-tools do.
    /// </para>
    /// </summary>
    internal static class CheckpointStore
    {
        /// <summary>Maximum entries kept before the least-recently-accessed is evicted.</summary>
        internal const int DefaultCapacity = 20;

        static readonly List<CheckpointStoreEntry> _entries = new();
        static readonly Dictionary<string, CheckpointStoreEntry> _index = new();

        /// <summary>Number of checkpoints currently stored.</summary>
        internal static int Count => _entries.Count;

        /// <summary>
        /// Stored entries in insertion order (oldest insert first). LRU affects only eviction, not
        /// display position — reverse at the call site if newest-first is wanted.
        /// </summary>
        internal static IReadOnlyList<CheckpointStoreEntry> Recent => _entries.AsReadOnly();

        /// <summary>
        /// Store (or overwrite) <paramref name="entry"/>. A duplicate
        /// <see cref="CheckpointStoreEntry.CheckpointId"/> replaces the prior entry and refreshes
        /// recency, so a re-submission is treated as "latest data wins" rather than a silent no-op.
        /// Evicts the least-recently-accessed entry when over capacity.
        /// </summary>
        internal static void Store(CheckpointStoreEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));

            if (_index.TryGetValue(entry.CheckpointId, out var existing))
            {
                _entries.Remove(existing);
                _index.Remove(entry.CheckpointId);
            }

            if (string.IsNullOrEmpty(entry.LastAccessedUtc))
                entry.LastAccessedUtc = entry.Timestamp ?? DateTime.UtcNow.ToString("o");

            _entries.Add(entry);
            _index[entry.CheckpointId] = entry;

            while (_entries.Count > DefaultCapacity)
            {
                // Drop the entry with the oldest LastAccessedUtc (string comparison is valid for
                // ISO-8601 UTC), not blindly the first-inserted one — keeps checkpoints an agent is
                // actively delta-comparing against alive even when many newer inserts arrive.
                var oldest = _entries[0];
                for (int i = 1; i < _entries.Count; i++)
                {
                    if (string.CompareOrdinal(_entries[i].LastAccessedUtc, oldest.LastAccessedUtc) < 0)
                        oldest = _entries[i];
                }
                _entries.Remove(oldest);
                _index.Remove(oldest.CheckpointId);
            }
        }

        /// <summary>
        /// Look up <paramref name="checkpointId"/>, or null when it is not in the store. Bumps the
        /// entry's access clock so active checkpoints survive LRU eviction under pressure.
        /// </summary>
        internal static CheckpointStoreEntry? Get(string checkpointId)
        {
            if (checkpointId == null) return null;
            if (!_index.TryGetValue(checkpointId, out var entry)) return null;
            entry.LastAccessedUtc = DateTime.UtcNow.ToString("o");
            return entry;
        }

        /// <summary>Drop every entry (test seam; also clears on assembly reload via static re-init).</summary>
        internal static void Clear()
        {
            _entries.Clear();
            _index.Clear();
        }
    }
}
#endif
