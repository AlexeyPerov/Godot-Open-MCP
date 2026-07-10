#if TOOLS
#nullable enable
using System.Collections.Generic;
using Godot;
using GodotOpenMcp.Verify.Core;
using GodotOpenMcp.Verify.Fixes;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Orchestrates a non-dry-run <c>apply_fix</c> run WITH safe auto-fix rollback (P3.7).
    ///
    /// <para>
    /// The generic <see cref="GatePolicy.Execute"/> already does checkpoint → mutate → validate → delta,
    /// and <c>delta.NewErrors &gt; 0</c> means the fix made things worse. But the gate only FAILS the
    /// dispatch under Enforce — it does not undo the fix. This runner adds the undo step so the gate's
    /// trust contract holds: a fix that fails or introduces new errors (or throws) is rolled back to its
    /// pre-fix state, and the envelope reports <c>rolledBack</c> + the restored paths via the
    /// <see cref="GateDispatchResult.RolledBack"/> / <see cref="GateDispatchResult.RestoredPaths"/> fields.
    /// </para>
    ///
    /// <para>
    /// Ported (copy for the rollback contract; adapt for the path model) from Unity Open MCP's
    /// <c>ApplyFixGateRunner</c>. Intentional deltas for Godot:
    /// <list type="bullet">
    ///   <item><b>No companion <c>.meta</c>.</b> Unity snapshots the issue asset + its <c>.meta</c>
    ///   (every asset has one, and several fixes rewrite it). Godot has no <c>.meta</c> companions, so
    ///   <see cref="PredictTouchedPaths"/> snapshots only the issue's own path.</item>
    ///   <item><b><c>ProjectSettings.GlobalizePath</c> for res://→absolute.</b> Unity resolves against
    ///   <c>Application.dataPath</c>; Godot's canonical <c>res://</c>→OS-path resolver is
    ///   <c>ProjectSettings.GlobalizePath</c> (the same call <c>BridgeSession</c> uses for the project
    ///   root). Restored paths are normalized back to <c>res://</c> via
    ///   <see cref="NormalizeRestoredPaths"/>.</item>
    ///   <item><b>No <c>AssetDatabase.Refresh</c>.</b> Unity reimports after a restore; Godot's editor
    ///   picks up filesystem changes on its own scan. The gate's re-validate step re-reads the file from
    ///   disk, so a restored file is observed in its pre-fix state.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// High-confidence threshold for rollback = <c>delta.NewErrors &gt; 0</c> (matches the gate's own
    /// failure condition). New WARNINGS do not trigger rollback — they are informational, and several
    /// fixes legitimately change a project in ways that surface new warnings.
    /// </para>
    /// </summary>
    internal static class ApplyFixGateRunner
    {
        internal static GateDispatchResult Execute(
            string body, string gateMode, string[]? pathsHint)
        {
            var mode = GatePolicy.ParseMode(gateMode);
            var issueId = JsonBody.GetString(body, "issue_id");

            // Predict the file the fix may touch and snapshot it BEFORE the gate runs the mutation.
            // predictedPaths is null when the issue id cannot be parsed (no snapshot ⇒ no rollback; the
            // gate still runs).
            var predictedPaths = PredictTouchedPaths(issueId);
            var rollback = new FixRollback();
            if (predictedPaths != null && predictedPaths.Length > 0)
                rollback.Snapshot(predictedPaths);

            GateDispatchResult result;
            try
            {
                // Reuse the exact gate path (checkpoint → apply → validate → delta). ApplyFixTool.Execute
                // runs the provider's Apply.
                result = GatePolicy.Execute(mode, pathsHint,
                    () => ApplyFixTool.Execute(body));
            }
            catch
            {
                // The fix threw an exception (the gate wraps the mutation in a try/catch internally, so
                // this only fires for checkpoint failures). Roll back to be safe, then rethrow so the
                // bridge builds the fault envelope.
                if (rollback.HasSnapshot)
                    rollback.Restore();
                rollback.Discard();
                throw;
            }

            // Decide whether to roll back. Two triggers:
            //   1. The mutation itself failed (provider returned !Success or threw before the validate
            //      step — GatePolicy marks these Outcome=Failed with GateFailed=true but no delta).
            //   2. Under ENFORCE, the gate detected new errors after the fix. (Warn mode never rolls back
            //      — the operator asked for report-only; Off mode has no delta to check.)
            bool mutationFailed = result.Mutation != null && !result.Mutation.Success;
            bool gateIntroducedErrors = mode == GateMode.Enforce
                && result.GateRan
                && result.Delta != null
                && result.Delta.NewErrors > 0;

            if ((mutationFailed || gateIntroducedErrors) && rollback.HasSnapshot)
            {
                var restore = rollback.Restore();

                result.RolledBack = true;
                result.RollbackReason = mutationFailed
                    ? "fix failed to apply — restored touched files to pre-fix state"
                    : $"fix introduced {result.Delta!.NewErrors} new error(s) under enforce — restored touched files to pre-fix state";
                // Normalize restored paths to the res:// form the rest of the tool surface uses, so
                // callers can compare directly against issue asset paths.
                result.RestoredPaths = NormalizeRestoredPaths(restore.RestoredPaths);

                // Augment agent guidance so the next step is clear.
                var steps = result.AgentNextSteps == null
                    ? new List<string>()
                    : new List<string>(result.AgentNextSteps);
                steps.Add("The fix was rolled back — no project change remains. Inspect the issue manually before retrying.");
                result.AgentNextSteps = steps.ToArray();
            }

            rollback.Discard();
            return result;
        }

        /// <summary>
        /// Predict the absolute path a fix may touch from the issue id. The issue's asset path is
        /// <c>res://</c>-relative; <see cref="ProjectSettings.GlobalizePath"/> resolves it against the
        /// project root. Godot has no <c>.meta</c> companion, so only the asset itself is snapshotted.
        /// Returns null when the issue id cannot be parsed or the asset path is empty.
        /// </summary>
        static string[]? PredictTouchedPaths(string? issueId)
        {
            if (!IssueKey.TryParse(issueId, out _, out _, out var assetPath, out _))
                return null;
            if (string.IsNullOrEmpty(assetPath)) return null;

            var abs = ResolveAbsolute(assetPath!);
            if (abs == null) return null;
            return new[] { abs };
        }

        /// <summary>
        /// Resolve a <c>res://</c>-relative path to an absolute OS path. Uses
        /// <see cref="ProjectSettings.GlobalizePath"/> (the canonical resolver); falls back to joining
        /// against <see cref="BridgeSession.ProjectPath"/> when GlobalizePath returns empty (e.g. running
        /// outside a project in a test). Returns null when neither resolves to a non-empty path.
        /// </summary>
        static string? ResolveAbsolute(string resPath)
        {
            // GlobalizePath is the canonical res:// → absolute resolver. It can return the input unchanged
            // or empty when the project root is not known (headless test without a project); fall back to
            // the cached project path so the snapshot still works in that context.
            var abs = ProjectSettings.GlobalizePath(resPath);
            if (!string.IsNullOrEmpty(abs) && System.IO.File.Exists(abs))
                return abs;

            var projectRoot = BridgeSession.ProjectPath;
            if (!string.IsNullOrEmpty(projectRoot))
            {
                // res://  → <projectRoot>/  (strip the res:// prefix, join, normalize).
                var relative = resPath.StartsWith("res://", System.StringComparison.Ordinal)
                    ? resPath.Substring("res://".Length)
                    : resPath;
                var joined = System.IO.Path.GetFullPath(System.IO.Path.Combine(projectRoot!, relative));
                if (System.IO.File.Exists(joined))
                    return joined;
            }
            return null;
        }

        /// <summary>
        /// Convert absolute restored paths back to the <c>res://</c> form so the envelope matches the asset
        /// paths used everywhere else in the API. Falls back to the absolute path when it does not lie under
        /// the project root.
        /// </summary>
        static string[] NormalizeRestoredPaths(string[]? restored)
        {
            if (restored == null || restored.Length == 0) return restored!;
            var projectRoot = BridgeSession.ProjectPath;
            var result = new string[restored.Length];
            for (int i = 0; i < restored.Length; i++)
            {
                var norm = restored[i].Replace('\\', '/');
                if (!string.IsNullOrEmpty(projectRoot))
                {
                    var root = projectRoot!.Replace('\\', '/').TrimEnd('/');
                    if (norm.StartsWith(root + "/", System.StringComparison.Ordinal))
                    {
                        result[i] = "res://" + norm.Substring((root + "/").Length);
                        continue;
                    }
                }
                result[i] = norm;
            }
            return result;
        }
    }
}
#endif
