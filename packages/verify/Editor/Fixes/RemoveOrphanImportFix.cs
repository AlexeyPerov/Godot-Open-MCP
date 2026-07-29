#nullable enable
using System;
using System.IO;
using GodotOpenMcp.Verify.Core;

namespace GodotOpenMcp.Verify.Fixes
{
    /// <summary>
    /// Resolves <c>import_health|orphan_import</c> by deleting an orphan <c>.import</c> sidecar
    /// whose source asset is already gone. Ported (adapt) from Unity Open MCP's
    /// <c>RemoveOrphanMetaFix</c>.
    /// </summary>
    public sealed class RemoveOrphanImportFix : IFixProvider
    {
        private readonly Func<string, bool> _pathExists;
        private readonly Func<string, bool> _deleteFile;
        private readonly Func<string, string> _globalizePath;

        public RemoveOrphanImportFix()
            : this(File.Exists, p => { File.Delete(p); return true; }, GlobalizeResPath) { }

        internal RemoveOrphanImportFix(
            Func<string, bool> pathExists,
            Func<string, bool> deleteFile,
            Func<string, string> globalizePath)
        {
            _pathExists = pathExists ?? throw new ArgumentNullException(nameof(pathExists));
            _deleteFile = deleteFile ?? throw new ArgumentNullException(nameof(deleteFile));
            _globalizePath = globalizePath ?? throw new ArgumentNullException(nameof(globalizePath));
        }

        public string FixId => "remove_orphan_import";

        public bool CanFix(string issueId)
        {
            if (!IssueKey.TryParse(issueId, out var ruleId, out _, out _, out var issueCode))
                return false;
            return ruleId == Rules.ImportHealth.ImportHealthRule.RuleId
                && issueCode == Rules.ImportHealth.IssueCodes.OrphanImport;
        }

        public FixDescription Describe(string issueId)
        {
            IssueKey.TryParse(issueId, out _, out _, out var assetPath, out _);
            return new FixDescription
            {
                FixId = FixId,
                IssueId = issueId,
                AssetPath = assetPath ?? "",
                Description = $"Delete orphan import sidecar '{assetPath}' (its source file is already gone). No asset data is lost.",
                Safe = true,
            };
        }

        public FixResult Apply(string issueId)
        {
            if (!IssueKey.TryParse(issueId, out _, out _, out var sidecarPath, out _))
                return Failed($"Cannot parse issue id: {issueId}");

            if (string.IsNullOrEmpty(sidecarPath))
                return Failed("Issue id contains an empty asset path.");

            if (!sidecarPath.EndsWith(".import", StringComparison.OrdinalIgnoreCase))
                return Failed($"remove_orphan_import expects a .import sidecar path, got '{sidecarPath}'.");

            var osPath = _globalizePath(sidecarPath);
            if (string.IsNullOrEmpty(osPath))
                return Failed($"Could not resolve '{sidecarPath}' to a filesystem path.");

            // Derive the companion source path by stripping the .import suffix.
            var companionRes = sidecarPath.Substring(0, sidecarPath.Length - ".import".Length);
            var companionOs = _globalizePath(companionRes);

            if (!string.IsNullOrEmpty(companionOs) && _pathExists(companionOs))
            {
                return new FixResult
                {
                    Success = true,
                    Description = $"Companion source exists at '{companionRes}' — '{sidecarPath}' is not orphaned. No change made.",
                    TouchedPaths = null,
                };
            }

            if (!_pathExists(osPath))
            {
                return new FixResult
                {
                    Success = true,
                    Description = $"Orphan sidecar '{sidecarPath}' no longer exists — already resolved.",
                    TouchedPaths = null,
                };
            }

            try
            {
                if (!_deleteFile(osPath))
                    return Failed($"Could not delete '{sidecarPath}'.");
            }
            catch (Exception e)
            {
                return Failed($"Could not delete '{sidecarPath}': {e.Message}");
            }

            return new FixResult
            {
                Success = true,
                Description = $"Deleted orphan import sidecar '{sidecarPath}'.",
                TouchedPaths = new[] { sidecarPath },
            };
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
    }
}
