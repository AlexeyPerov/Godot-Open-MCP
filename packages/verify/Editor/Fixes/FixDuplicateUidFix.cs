#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using GodotOpenMcp.Verify.Core;

namespace GodotOpenMcp.Verify.Fixes
{
    /// <summary>
    /// Resolves <c>import_health|duplicate_uid</c> by re-issuing a fresh <c>uid://</c> on one
    /// conflicting <c>.import</c> sidecar. The caller must pass <c>keep_path</c> naming the sidecar
    /// that retains the colliding uid. Ported (adapt) from Unity Open MCP's <c>FixDuplicateGuidFix</c>.
    /// </summary>
    public sealed class FixDuplicateUidFix : IFixProvider
    {
        private readonly Func<string, string?> _readFileText;
        private readonly Action<string, string> _writeFileText;
        private readonly Func<string, string> _globalizePath;
        private readonly Func<string> _generateUid;

        public FixDuplicateUidFix()
            : this(File.ReadAllText, File.WriteAllText, GlobalizeResPath, GenerateLiveUid) { }

        internal FixDuplicateUidFix(
            Func<string, string?> readFileText,
            Action<string, string> writeFileText,
            Func<string, string> globalizePath,
            Func<string> generateUid)
        {
            _readFileText = readFileText ?? throw new ArgumentNullException(nameof(readFileText));
            _writeFileText = writeFileText ?? throw new ArgumentNullException(nameof(writeFileText));
            _globalizePath = globalizePath ?? throw new ArgumentNullException(nameof(globalizePath));
            _generateUid = generateUid ?? throw new ArgumentNullException(nameof(generateUid));
        }

        public string FixId => "fix_duplicate_uid";

        public bool CanFix(string issueId)
        {
            if (!IssueKey.TryParse(issueId, out var ruleId, out _, out _, out var issueCode))
                return false;
            return ruleId == Rules.ImportHealth.ImportHealthRule.RuleId
                && issueCode == Rules.ImportHealth.IssueCodes.DuplicateUid;
        }

        public FixDescription Describe(string issueId)
        {
            IssueKey.TryParse(issueId, out _, out _, out var assetPath, out _);
            return new FixDescription
            {
                FixId = FixId,
                IssueId = issueId,
                AssetPath = assetPath ?? "",
                Description =
                    $"Re-issue a fresh uid:// for sidecar '{assetPath}' so it no longer collides. " +
                    "Pass keep_path naming the sidecar that should RETAIN the uid; apply this fix on an issue anchored on the OTHER sidecar.",
                Safe = false,
            };
        }

        public FixResult Apply(string issueId) => Apply(issueId, null);

        public FixResult Apply(string issueId, string? keepPath)
        {
            if (!IssueKey.TryParse(issueId, out _, out _, out var sidecarPath, out _))
                return Failed($"Cannot parse issue id: {issueId}");

            if (string.IsNullOrEmpty(sidecarPath))
                return Failed("Issue id contains an empty asset path.");

            if (!sidecarPath.EndsWith(".import", StringComparison.OrdinalIgnoreCase))
                return Failed($"fix_duplicate_uid expects a .import sidecar path, got '{sidecarPath}'.");

            if (string.IsNullOrWhiteSpace(keepPath))
                return Failed("fix_duplicate_uid requires keep_path naming the sidecar that retains the uid.");

            var keep = keepPath!.Trim();
            if (!keep.StartsWith("res://", StringComparison.Ordinal))
                return Failed($"keep_path must be res://-rooted, got '{keepPath}'.");

            if (string.Equals(sidecarPath, keep, StringComparison.Ordinal))
                return Failed(
                    $"Issue is anchored on keep_path '{keep}'. Apply fix_duplicate_uid on an issue for a DIFFERENT conflicting sidecar.");

            var osPath = _globalizePath(sidecarPath);
            if (string.IsNullOrEmpty(osPath))
                return Failed($"Could not resolve '{sidecarPath}' to a filesystem path.");

            string text;
            try
            {
                var raw = _readFileText(osPath);
                if (raw == null)
                    return Failed($"Could not read '{sidecarPath}'.");
                text = raw;
            }
            catch (Exception e)
            {
                return Failed($"Could not read '{sidecarPath}': {e.Message}");
            }

            var decl = Rules.ImportHealth.ImportFileParser.Parse(sidecarPath, text);
            if (string.IsNullOrEmpty(decl.Uid))
                return Failed($"Sidecar '{sidecarPath}' has no uid= field to rewrite.");

            var newUid = _generateUid();
            var newText = RewriteUidField(text, decl.Uid, newUid);

            try
            {
                _writeFileText(osPath, newText);
            }
            catch (Exception e)
            {
                return Failed($"Could not write '{sidecarPath}': {e.Message}");
            }

            return new FixResult
            {
                Success = true,
                Description = $"Re-issued uid for '{sidecarPath}' ({decl.Uid} → {newUid}). Kept uid on '{keep}'.",
                TouchedPaths = new[] { sidecarPath },
            };
        }

        internal static string RewriteUidField(string text, string oldUid, string newUid)
        {
            var pattern = new Regex(@"uid=""([^""]*)""", RegexOptions.CultureInvariant);
            var replaced = false;
            return pattern.Replace(text, m =>
            {
                if (replaced || m.Groups[1].Value != oldUid) return m.Value;
                replaced = true;
                return $"uid=\"{newUid}\"";
            });
        }

        private static FixResult Failed(string description) => new()
        {
            Success = false,
            Description = description,
            TouchedPaths = null,
        };

        private static string GlobalizeResPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return assetPath;
            if (!assetPath.StartsWith("res://", StringComparison.Ordinal)) return assetPath;
#if TOOLS
            var abs = Godot.ProjectSettings.GlobalizePath(assetPath);
            return string.IsNullOrEmpty(abs) ? assetPath : abs;
#else
            return assetPath;
#endif
        }

        private static string GenerateLiveUid()
        {
#if TOOLS
            var id = Godot.ResourceUid.CreateId();
            return Godot.ResourceUid.IdToText(id);
#else
            return "uid://testgenerated001";
#endif
        }
    }
}
