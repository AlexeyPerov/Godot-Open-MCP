#nullable enable
using System;
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
        private readonly Rules.BrokenReferences.IResourceResolver _resolver;
        private readonly Func<string, string?> _readFileText;
        private readonly Action<string, string> _writeFileText;
        private readonly Func<string, string> _globalizePath;

        /// <summary>Production constructor: live resolver, real file I/O, live <c>res://</c> resolution.</summary>
        public RemoveMissingScriptFix()
            : this(GetLiveResolver(), File.ReadAllText, File.WriteAllText, GlobalizeResPath) { }

        /// <summary>
        /// Testable constructor. Every Godot-coupled dependency is a seam so the fix stays pure-managed
        /// and runs in the binary-less xUnit host: <paramref name="resolver"/> decides whether a script
        /// reference is broken (same model the rule uses), and <paramref name="globalizePath"/> maps a
        /// <c>res://</c> asset path to an OS path for the read/write.
        /// </summary>
        internal RemoveMissingScriptFix(
            Rules.BrokenReferences.IResourceResolver resolver,
            Func<string, string?> readFileText,
            Action<string, string> writeFileText,
            Func<string, string> globalizePath)
        {
            _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
            _readFileText = readFileText ?? throw new ArgumentNullException(nameof(readFileText));
            _writeFileText = writeFileText ?? throw new ArgumentNullException(nameof(writeFileText));
            _globalizePath = globalizePath ?? throw new ArgumentNullException(nameof(globalizePath));
        }

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

            // The issue key carries only ruleId|severity|assetPath|issueCode — no node or line — so the
            // fix must re-derive WHICH attachment is broken. It does that the same way the rule does:
            // parse the node→script attachments and the [ext_resource] declarations, then pick the first
            // attachment whose declaration fails to resolve through the shared IResourceResolver.
            //
            // Selecting by "first line that looks like a script attachment" (the previous behavior) is
            // unsound: in a scene where node A has a healthy script and node B has the broken one, it
            // removes A's attachment — and then, because no line references A's id any more, deletes A's
            // [ext_resource] declaration too. That silently strips a working script, leaves the reported
            // issue in place (so the fix is not idempotent), and the gate cannot catch it because no rule
            // reports "a node lost its script", so NewErrors stays 0 and nothing is rolled back.
            //
            // The asset path is res://-rooted; System.IO cannot resolve that scheme, so it is globalized
            // to an OS path for the read/write while TouchedPaths keeps the res:// form the gate expects.
            var osPath = _globalizePath(assetPath!);
            if (string.IsNullOrEmpty(osPath))
                return Failed($"Could not resolve '{assetPath}' to a filesystem path.");

            string text;
            try
            {
                var raw = _readFileText(osPath);
                if (raw == null)
                    return Failed($"Could not read '{assetPath}'.");
                text = raw;
            }
            catch (System.Exception e)
            {
                return Failed($"Could not read '{assetPath}': {e.Message}");
            }

            Rules.BrokenReferences.SceneRefParseResult refs;
            Rules.MissingScripts.NodeScriptScanResult nodes;
            try
            {
                refs = Rules.BrokenReferences.SceneRefParser.Parse(text);
                nodes = Rules.MissingScripts.NodeScriptScanner.Parse(text);
            }
            catch (System.Exception e)
            {
                return Failed($"Could not parse '{assetPath}': {e.Message}");
            }

            // Last-wins index, tolerant of a duplicated id (a malformed asset must not throw here).
            var byId = new Dictionary<string, Rules.BrokenReferences.ExtResourceDecl>();
            foreach (var decl in refs.ExtResources) byId[decl.Id] = decl;

            var lines = text.Split('\n');

            // First attachment that is genuinely broken. Two cases count as broken, matching the rule:
            // the declaration resolves to nothing, or the id was never declared at all (dangling).
            var scriptLineIndex = -1;
            string scriptExtId = "";
            foreach (var attachment in nodes.Attachments)
            {
                // A declared id that resolves is healthy; an id that resolves to nothing, or that was
                // never declared in this file at all (dangling), is broken.
                bool broken;
                if (byId.TryGetValue(attachment.ScriptExtId, out var decl))
                    broken = IsExtResourceMissing(decl);
                else
                    broken = true;
                if (!broken) continue;

                // ScriptLine is 1-based; verify it still looks like the attachment before trusting it.
                var idx = attachment.ScriptLine - 1;
                if (idx < 0 || idx >= lines.Length || !ReferencesExtId(lines[idx], attachment.ScriptExtId))
                    continue;

                scriptLineIndex = idx;
                scriptExtId = attachment.ScriptExtId;
                break;
            }

            if (scriptLineIndex < 0)
            {
                return new FixResult
                {
                    Success = true,
                    Description = $"No broken `script = ExtResource(\"...\")` attachment found in '{assetPath}'. The issue may have already been resolved.",
                    TouchedPaths = null,
                };
            }

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
                _writeFileText(osPath, newText);
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
        /// The rule's false-positive guard, applied to a script attachment's <c>[ext_resource]</c>: an
        /// identifier is "ok" when it is present and resolves, so flag only when every present identifier
        /// fails. A stale <c>path=</c> with a live <c>uid=</c> (Godot's normal state after relocating an
        /// asset) must NOT count as broken. Kept byte-identical in behavior to
        /// <c>MissingScriptsRule.IsExtResourceMissing</c> so the fix can never disagree with the rule that
        /// reported the issue.
        /// </summary>
        private bool IsExtResourceMissing(Rules.BrokenReferences.ExtResourceDecl ext)
        {
            var uidPresent = !string.IsNullOrEmpty(ext.Uid);
            var pathPresent = !string.IsNullOrEmpty(ext.Path);
            if (!uidPresent && !pathPresent) return false; // malformed header; not this fix's concern

            var uidOk = uidPresent && _resolver.UidExists(ext.Uid);
            var pathOk = pathPresent && _resolver.PathExists(ext.Path);
            return !uidOk && !pathOk;
        }

        /// <summary>
        /// Live resolver, behind the package's single <c>#if TOOLS</c> boundary — identical pattern to
        /// <c>MissingScriptsRule.GetLiveResolver</c>. Without TOOLS (the binary-less test host) there is
        /// no live resolver; tests always use the internal constructor, so the fallback only needs to be
        /// safe, and "resolves nothing as broken" is the conservative choice for a destructive fix.
        /// </summary>
        private static Rules.BrokenReferences.IResourceResolver GetLiveResolver()
        {
#if TOOLS
            return Rules.BrokenReferences.LiveResourceResolver.Instance;
#else
            return NullResolver.Instance;
#endif
        }

#if !TOOLS
        private sealed class NullResolver : Rules.BrokenReferences.IResourceResolver
        {
            internal static readonly NullResolver Instance = new();
            public bool PathExists(string? resPath) => true;
            public bool UidExists(string? uid) => true;
        }
#endif

        /// <summary>
        /// Resolve a <c>res://</c> asset path to an OS path. <c>ProjectSettings.GlobalizePath</c> is
        /// Godot's canonical resolver; a non-<c>res://</c> path (a test fixture using a real temp path)
        /// passes through unchanged.
        /// </summary>
        private static string GlobalizeResPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return assetPath;
            if (!assetPath.StartsWith("res://", System.StringComparison.Ordinal)) return assetPath;
#if TOOLS
            var abs = Godot.ProjectSettings.GlobalizePath(assetPath);
            return string.IsNullOrEmpty(abs) ? assetPath : abs;
#else
            return assetPath;
#endif
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
