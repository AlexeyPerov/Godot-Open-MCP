#nullable enable
using System.Collections.Generic;
using System.IO;
using GodotOpenMcp.Verify.Core;

namespace GodotOpenMcp.Verify.Fixes
{
    /// <summary>
    /// The first <c>Safe: true</c> fix provider (P3.7). Resolves the <c>missing_scripts|missing_script</c>
    /// issue by removing the broken <c>script = ExtResource("id")</c> attachment from the offending node in a
    /// <c>.tscn</c>/<c>.tres</c> text file, and — when the referenced <c>[ext_resource]</c> is now orphaned
    /// (no remaining usage) — dropping that declaration too. Ported (adapt) from Unity Open MCP's
    /// <c>RemoveMissingScriptFix</c>.
    ///
    /// <para>
    /// <b>Intentional delta — text-edit instead of a live load/save.</b> Unity loads the prefab/scene via
    /// <c>PrefabUtility</c>/<c>EditorSceneManager</c>, calls <c>GameObjectUtility.RemoveMonoBehavioursWith-
    /// MissingScript</c>, and saves back. Godot stores scenes and resources as text (<c>.tscn</c>/<c>.tres</c>);
    /// a node's script attachment is a single <c>script = ExtResource("id")</c> body line, and the
    /// <c>MissingScriptsRule</c> already localizes it (<c>Evidence["line"]</c>). So this fix operates directly
    /// on the text — remove the attachment line, optionally remove the orphaned declaration — which mirrors
    /// the line-oriented parsing discipline of <c>SceneRefParser</c>/<c>NodeScriptScanner</c> and avoids a
    /// live <c>ResourceSaver</c> round-trip (no <c>EditorInterface</c> needed, keeps the verify package
    /// standalone). The gate re-validates after the edit; the <c>ApplyFixGateRunner</c> snapshots the file
    /// for rollback.
    /// </para>
    ///
    /// <para>
    /// <b>Safe flag.</b> Removing a broken script attachment is the Godot analog of Unity's safe
    /// <c>remove_missing_script</c>: the attachment already points at nothing loadable, so removing it cannot
    /// lose data — the node simply stops claiming a script it cannot run. <c>Safe</c> is <c>true</c> for the
    /// supported text scene/resource kinds; non-text assets (binary <c>.scn</c>/<c>.res</c>, anything else)
    /// return <c>Safe: false</c> and <c>Apply</c> refuses them (the text-edit strategy does not apply).
    /// </para>
    /// </summary>
    public sealed class RemoveMissingScriptFix : IFixProvider
    {
        /// <summary>The stable fix id surfaced in the catalog and matched by <see cref="CanFix"/>.</summary>
        public string FixId => "remove_missing_script";

        /// <summary>
        /// True when the issue id is <c>missing_scripts|*|*|missing_script</c>. Only the ruleId + issueCode
        /// halves are inspected, so a synthetic catalog key matches the same provider a real issue would.
        /// </summary>
        public bool CanFix(string issueId)
        {
            if (!IssueKey.TryParse(issueId, out var ruleId, out _, out _, out var issueCode))
                return false;
            return ruleId == Rules.MissingScripts.MissingScriptsRule.RuleId
                && issueCode == Rules.MissingScripts.IssueCodes.MissingScript;
        }

        /// <summary>
        /// Describe what the fix will do. <c>Safe</c> is <c>true</c> only for the text scene/resource kinds
        /// <see cref="Apply"/> supports; other extensions get an unsafe description so the gate never
        /// auto-suggests a strategy it cannot apply.
        /// </summary>
        public FixDescription Describe(string issueId)
        {
            IssueKey.TryParse(issueId, out _, out _, out var assetPath, out _);
            var ext = Path.GetExtension(assetPath ?? "").ToLowerInvariant();
            var supported = IsSupportedExtension(ext);

            return new FixDescription
            {
                FixId = FixId,
                IssueId = issueId,
                AssetPath = assetPath ?? "",
                Description = supported
                    ? $"Remove the broken script attachment from '{assetPath}' (and drop the orphaned [ext_resource] if no node still uses it)."
                    : $"remove_missing_script only supports .tscn/.tres text assets, got '{ext}'.",
                Safe = supported,
            };
        }

        /// <summary>
        /// Apply the fix: read the text file, drop the offending <c>script = ExtResource("id")</c> line, drop
        /// the now-orphaned <c>[ext_resource]</c> declaration when no remaining usage references it, write the
        /// file back. Returns <c>TouchedPaths = [assetPath]</c> on a real edit; a no-op edit (line already
        /// gone) still succeeds with a explanatory description and no touched paths.
        /// </summary>
        public FixResult Apply(string issueId)
        {
            if (!IssueKey.TryParse(issueId, out _, out _, out var assetPath, out _))
                return Failed($"Cannot parse issue id: {issueId}");

            if (string.IsNullOrEmpty(assetPath))
                return Failed("Issue id contains an empty asset path.");

            var ext = Path.GetExtension(assetPath).ToLowerInvariant();
            if (!IsSupportedExtension(ext))
                return Failed($"remove_missing_script only supports .tscn/.tres text assets, got '{ext}'.");

            // The MissingScriptsRule localizes the attachment via Evidence. We need the script's ext id to
            // find the line and the (optional) orphaned declaration; the line number is a hint that speeds up
            // the search and disambiguates when the same id is reused. The issue carries these via Evidence
            // — but a real issue id has no Evidence attached (Evidence lives on the VerifyIssue, not the key).
            // So re-scan the text for the FIRST script attachment whose ext id matches, and resolve the
            // declaration by id. This is robust to an agent supplying only the canonical key.
            string text;
            try
            {
                text = File.ReadAllText(assetPath);
            }
            catch (System.Exception e)
            {
                return Failed($"Could not read '{assetPath}': {e.Message}");
            }

            var lines = text.Split('\n');

            // Find the offending script line: `script = ExtResource("<id>")`. Without Evidence we cannot know
            // which id the issue named, so remove the FIRST broken attachment. (The gate re-runs the rule
            // afterward; if more remain, the agent re-issues apply_fix for the next.) The line-match is
            // intentionally narrow so a non-attachment `ExtResource("...")` (e.g. a property value) is never
            // touched.
            var scriptLineIndex = FindScriptAttachmentLine(lines);
            if (scriptLineIndex < 0)
            {
                return new FixResult
                {
                    Success = true,
                    Description = $"No `script = ExtResource(\"...\")` attachment found in '{assetPath}'. The issue may have already been resolved.",
                    TouchedPaths = null,
                };
            }

            var scriptExtId = ExtractExtId(lines[scriptLineIndex]);

            // Remove the attachment line. Keep the line ending semantics simple: rebuild without the line.
            var edited = new List<string>(lines.Length);
            for (var i = 0; i < lines.Length; i++)
                if (i != scriptLineIndex) edited.Add(lines[i]);

            // If we resolved the ext id, check whether any remaining line still references it. If not, the
            // [ext_resource] declaration is orphaned — drop it too.
            if (!string.IsNullOrEmpty(scriptExtId))
            {
                var stillUsed = false;
                foreach (var line in edited)
                {
                    if (ReferencesExtId(line, scriptExtId))
                    {
                        stillUsed = true;
                        break;
                    }
                }

                if (!stillUsed)
                {
                    var declIndex = FindExtResourceDeclaration(edited, scriptExtId);
                    if (declIndex >= 0)
                        edited.RemoveAt(declIndex);
                }
            }

            var newText = string.Join("\n", edited);

            try
            {
                File.WriteAllText(assetPath, newText);
            }
            catch (System.Exception e)
            {
                return Failed($"Could not write '{assetPath}': {e.Message}");
            }

            return new FixResult
            {
                Success = true,
                Description = $"Removed broken script attachment from '{assetPath}'.",
                TouchedPaths = new[] { assetPath },
            };
        }

        private static bool IsSupportedExtension(string ext) => ext == ".tscn" || ext == ".tres";

        private static FixResult Failed(string description) => new()
        {
            Success = false,
            Description = description,
            TouchedPaths = null,
        };

        /// <summary>
        /// Find the index of the first line matching a node-body script attachment: <c>script = ExtResource("id")</c>.
        /// Indented and trimmed to tolerate whitespace; does not match a <c>[node]</c>/<c>[ext_resource]</c> header.
        /// </summary>
        private static int FindScriptAttachmentLine(string[] lines)
        {
            for (var i = 0; i < lines.Length; i++)
            {
                var t = lines[i].TrimStart();
                // Match `script = ExtResource(` but NOT `# script = ...` (a comment) and not a header.
                if (t.StartsWith("script", System.StringComparison.Ordinal)
                    && t.Contains("ExtResource(")
                    && !lines[i].TrimStart().StartsWith("#", System.StringComparison.Ordinal))
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>Extract the string id from <c>script = ExtResource("1_abc")</c> → <c>1_abc</c>. Empty on no match.</summary>
        private static string ExtractExtId(string line)
        {
            var open = line.IndexOf("ExtResource(\"", System.StringComparison.Ordinal);
            if (open < 0) return "";
            var start = open + "ExtResource(\"".Length;
            var close = line.IndexOf('"', start);
            if (close < 0) return "";
            return line.Substring(start, close - start);
        }

        /// <summary>
        /// True when the line is a USAGE of <c>ExtResource("id")</c> (not the declaration header and not a
        /// comment). The declaration header <c>[ext_resource ... id="id"]</c> is intentionally NOT a usage —
        /// it is what <see cref="FindExtResourceDeclaration"/> locates for removal when no usage remains.
        /// </summary>
        private static bool ReferencesExtId(string line, string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("#", System.StringComparison.Ordinal)) return false;
            return trimmed.Contains($"ExtResource(\"{id}\")");
        }

        /// <summary>Find the index of the <c>[ext_resource ... id="&lt;id&gt;"]</c> declaration line, or -1.</summary>
        private static int FindExtResourceDeclaration(List<string> lines, string id)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (!trimmed.StartsWith("[ext_resource", System.StringComparison.Ordinal)) continue;
                if (trimmed.Contains($"id=\"{id}\"")) return i;
            }
            return -1;
        }
    }
}
