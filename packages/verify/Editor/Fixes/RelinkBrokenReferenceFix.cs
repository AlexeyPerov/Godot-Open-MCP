#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using GodotOpenMcp.Verify.Core;

namespace GodotOpenMcp.Verify.Fixes
{
    /// <summary>
    /// Resolves <c>broken_references|broken_scene_reference</c> by repointing a broken
    /// <c>[ext_resource]</c> header's <c>path=</c>/<c>uid=</c> tokens to a caller-chosen target.
    /// Ported (adapt) from Unity Open MCP's <c>RelinkBrokenGuidFix</c>.
    /// </summary>
    public sealed class RelinkBrokenReferenceFix : IFixProvider
    {
        private readonly Rules.BrokenReferences.IResourceResolver _resolver;
        private readonly Func<string, string?> _readFileText;
        private readonly Action<string, string> _writeFileText;
        private readonly Func<string, string> _globalizePath;

        public RelinkBrokenReferenceFix()
            : this(GetLiveResolver(), File.ReadAllText, File.WriteAllText, GlobalizeResPath) { }

        internal RelinkBrokenReferenceFix(
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

        public string FixId => "relink_broken_reference";

        public bool CanFix(string issueId)
        {
            if (!IssueKey.TryParse(issueId, out var ruleId, out _, out _, out var issueCode))
                return false;
            return ruleId == Rules.BrokenReferences.BrokenReferencesRule.RuleId
                && issueCode == Rules.BrokenReferences.IssueCodes.BrokenSceneReference;
        }

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
                    ? $"Relink the broken [ext_resource] in '{assetPath}' to a caller-chosen target. " +
                      "Pass target_uid (or target_path) via apply_fix — use find_references/dependencies to pick the replacement."
                    : $"relink_broken_reference only supports .tscn/.tres text assets, got '{ext}'.",
                Safe = false,
            };
        }

        public FixResult Apply(string issueId) => Apply(issueId, null, null);

        public FixResult Apply(string issueId, string? targetUid, string? targetPath)
        {
            if (!IssueKey.TryParse(issueId, out _, out _, out var assetPath, out _))
                return Failed($"Cannot parse issue id: {issueId}");

            if (string.IsNullOrEmpty(assetPath))
                return Failed("Issue id contains an empty asset path.");

            var ext = Path.GetExtension(assetPath).ToLowerInvariant();
            if (!IsSupportedExtension(ext))
                return Failed($"relink_broken_reference only supports .tscn/.tres text assets, got '{ext}'.");

            var hasUid = !string.IsNullOrWhiteSpace(targetUid);
            var hasPath = !string.IsNullOrWhiteSpace(targetPath);
            if (!hasUid && !hasPath)
                return Failed("relink_broken_reference requires target_uid or target_path.");

            string resolvedPath = "";
            string resolvedUid = "";

            if (hasUid)
            {
                resolvedUid = NormalizeUid(targetUid!);
                if (!_resolver.UidExists(resolvedUid))
                    return Failed($"target_uid '{targetUid}' does not resolve.");
                // Path may remain empty when only uid is supplied — Godot accepts uid-only headers.
            }

            if (hasPath)
            {
                resolvedPath = targetPath!.Trim();
                if (!resolvedPath.StartsWith("res://", StringComparison.Ordinal))
                    return Failed($"target_path must be res://-rooted, got '{targetPath}'.");
                if (!_resolver.PathExists(resolvedPath))
                    return Failed($"target_path '{targetPath}' does not resolve.");
            }

            if (!hasUid && hasPath)
            {
                // uid optional when path resolves.
            }

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
            catch (Exception e)
            {
                return Failed($"Could not read '{assetPath}': {e.Message}");
            }

            Rules.BrokenReferences.SceneRefParseResult refs;
            try
            {
                refs = Rules.BrokenReferences.SceneRefParser.Parse(text);
            }
            catch (Exception e)
            {
                return Failed($"Could not parse '{assetPath}': {e.Message}");
            }

            var lines = text.Split('\n');
            var brokenDecl = FindFirstBrokenExtResource(refs);
            if (brokenDecl == null)
            {
                return new FixResult
                {
                    Success = true,
                    Description = $"No broken [ext_resource] declaration found in '{assetPath}'. The issue may have already been resolved.",
                    TouchedPaths = null,
                };
            }

            var lineIndex = brokenDecl.Line - 1;
            if (lineIndex < 0 || lineIndex >= lines.Length)
                return Failed($"Broken ext_resource line {brokenDecl.Line} is out of range in '{assetPath}'.");

            var newLine = RewriteExtResourceLine(
                lines[lineIndex],
                hasPath ? resolvedPath : null,
                hasUid ? resolvedUid : null);
            lines[lineIndex] = newLine;
            var newText = string.Join("\n", lines);

            try
            {
                _writeFileText(osPath, newText);
            }
            catch (Exception e)
            {
                return Failed($"Could not write '{assetPath}': {e.Message}");
            }

            var targetLabel = hasUid ? resolvedUid : resolvedPath;
            return new FixResult
            {
                Success = true,
                Description = $"Relinked ext_resource \"{brokenDecl.Id}\" in '{assetPath}' to {targetLabel}.",
                TouchedPaths = new[] { assetPath },
            };
        }

        private Rules.BrokenReferences.ExtResourceDecl? FindFirstBrokenExtResource(
            Rules.BrokenReferences.SceneRefParseResult refs)
        {
            foreach (var ext in refs.ExtResources)
            {
                if (IsExtResourceMissing(ext)) return ext;
            }
            return null;
        }

        private bool IsExtResourceMissing(Rules.BrokenReferences.ExtResourceDecl ext)
        {
            var uidPresent = !string.IsNullOrEmpty(ext.Uid);
            var pathPresent = !string.IsNullOrEmpty(ext.Path);
            if (!uidPresent && !pathPresent) return false;

            var uidOk = uidPresent && _resolver.UidExists(ext.Uid);
            var pathOk = pathPresent && _resolver.PathExists(ext.Path);
            return !uidOk && !pathOk;
        }

        internal static string RewriteExtResourceLine(string line, string? newPath, string? newUid)
        {
            var result = line;
            if (newPath != null)
            {
                if (AttrRegex("path").IsMatch(result))
                    result = AttrRegex("path").Replace(result, $"path=\"{newPath}\"");
                else
                    result = InsertAttr(result, $"path=\"{newPath}\"");
            }
            if (newUid != null)
            {
                if (AttrRegex("uid").IsMatch(result))
                    result = AttrRegex("uid").Replace(result, $"uid=\"{newUid}\"");
                else
                    result = InsertAttr(result, $"uid=\"{newUid}\"");
            }
            return result;
        }

        private static Regex AttrRegex(string key)
            => new Regex($@"\b{key}=""[^""]*""", RegexOptions.CultureInvariant);

        private static string InsertAttr(string line, string attr)
        {
            var close = line.LastIndexOf(']');
            if (close < 0) return line + " " + attr;
            return line.Insert(close, " " + attr);
        }

        private static string NormalizeUid(string raw)
        {
            var trimmed = raw.Trim();
            return trimmed.StartsWith("uid://", StringComparison.Ordinal) ? trimmed : "uid://" + trimmed;
        }

        private static bool IsSupportedExtension(string ext) => ext == ".tscn" || ext == ".tres";

        private static FixResult Failed(string description) => new()
        {
            Success = false,
            Description = description,
            TouchedPaths = null,
        };

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
