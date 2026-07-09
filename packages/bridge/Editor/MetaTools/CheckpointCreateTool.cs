#if TOOLS
#nullable enable
using System;
using System.Text;
using GodotOpenMcp.Verify.Core;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// <c>godot_open_mcp_checkpoint_create</c> handler — capture a project-health baseline over
    /// <c>res://</c> paths and stash it in <see cref="CheckpointStore"/> so a later
    /// <c>godot_open_mcp_delta</c> call can compare the post-mutation state against it. This is the
    /// explicit form of the gate's checkpoint step (the gate captures its own on-stack fingerprint
    /// per mutating dispatch; this tool exposes the capture so an agent can run the checkpoint →
    /// mutate → delta workflow across separate tool calls).
    ///
    /// <para>
    /// Ported (copy for the JSON shape; adapt for the Godot adapter surface) from Unity Open MCP's
    /// <c>CheckpointCreateTool</c>. Intentional deltas for v1:
    /// <list type="bullet">
    ///   <item><b>No <c>SelectRuleIds</c>.</b> Unity picks rules per asset extension; the Godot
    ///   <see cref="VerifyGateAdapter"/> has no rule-selection helper (see its class doc's "No rule
    ///   selection" delta), so <c>null</c> ruleIds runs every registered rule. The verify package
    ///   registers exactly three cheap rules (broken_references, missing_scripts, import_health), so
    ///   "all rules" is correct and inexpensive.</item>
    ///   <item><b><c>paths</c> is optional but recommended.</b> An empty/absent array runs against
    ///   the whole project. The bridge gate has no whole-project fallback for its own dispatch path
    ///   (<c>packages/bridge/AGENTS.md</c> §Gate policy), but this explicit meta-tool accepts an empty
    ///   scope and lets verify decide — the verify runner scans the configured scope paths, and an
    ///   empty scope yields an empty fingerprint (a valid "baseline of nothing"). Agents are
    ///   encouraged to pass the paths they intend to mutate.</item>
    ///   <item><b><see cref="BridgeJson.EscapeString"/> instead of a local <c>Esc()</c>.</b></item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Read-only w.r.t. project state (registered <c>isMutating:false</c>) — it writes only to the
    /// session-scoped in-memory <see cref="CheckpointStore"/>, never to assets. Structured failures:
    /// <c>checkpoint_error</c> (verify threw during capture).
    /// </para>
    /// </summary>
    internal static class CheckpointCreateTool
    {
        internal static ToolDispatchResult Execute(string body)
        {
            var paths = JsonBody.GetStringArray(body, "paths");
            var label = JsonBody.GetString(body, "label");

            CheckpointFingerprint checkpoint;
            try
            {
                // No SelectRuleIds in Godot (see class doc) — pass null so every registered rule
                // contributes a fingerprint.
                checkpoint = VerifyGateAdapter.CreateCheckpoint(
                    paths ?? Array.Empty<string>(), null);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("checkpoint_error", e.Message);
            }

            var entry = new CheckpointStoreEntry
            {
                CheckpointId = checkpoint.CheckpointId,
                Timestamp = DateTime.UtcNow.ToString("o"),
                Label = label,
                Paths = paths,
                Categories = checkpoint.Fingerprints != null
                    ? BuildCategoriesArray(checkpoint)
                    : Array.Empty<string>(),
                Fingerprint = checkpoint
            };
            CheckpointStore.Store(entry);

            return ToolDispatchResult.Ok(BuildResult(entry));
        }

        /// <summary>Snapshot the rule ids that contributed fingerprints, for the entry's Categories field.</summary>
        static string[] BuildCategoriesArray(CheckpointFingerprint checkpoint)
        {
            var keys = new string[checkpoint.Fingerprints.Count];
            int i = 0;
            foreach (var key in checkpoint.Fingerprints.Keys)
                keys[i++] = key;
            return keys;
        }

        /// <summary>
        /// Build the response JSON: <c>checkpointId</c> (the resume key), <c>timestamp</c>, and a
        /// per-rule <c>fingerprint</c> map of <c>errors</c> / <c>warnings</c> / <c>issueKeys[]</c>.
        /// </summary>
        static string BuildResult(CheckpointStoreEntry entry)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"checkpointId\":").Append(BridgeJson.EscapeString(entry.CheckpointId));
            sb.Append(",\"timestamp\":").Append(BridgeJson.EscapeString(entry.Timestamp));
            sb.Append(",\"fingerprint\":{");

            var fp = entry.Fingerprint;
            if (fp != null && fp.Fingerprints != null)
            {
                var first = true;
                foreach (var kvp in fp.Fingerprints)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(BridgeJson.EscapeString(kvp.Key)).Append(":{");
                    sb.Append("\"errors\":").Append(kvp.Value.Errors);
                    sb.Append(",\"warnings\":").Append(kvp.Value.Warnings);
                    sb.Append(",\"issueKeys\":[");
                    if (kvp.Value.IssueKeys != null)
                    {
                        var keyFirst = true;
                        foreach (var key in kvp.Value.IssueKeys)
                        {
                            if (!keyFirst) sb.Append(',');
                            keyFirst = false;
                            sb.Append(BridgeJson.EscapeString(key));
                        }
                    }
                    sb.Append("]}");
                }
            }

            sb.Append("}}");
            return sb.ToString();
        }
    }
}
#endif
